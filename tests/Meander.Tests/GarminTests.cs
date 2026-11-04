using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meander.Core;
using Meander.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Meander.Tests;

internal sealed class StubGarmin : HttpMessageHandler
{
    public readonly ConcurrentQueue<(HttpMethod Method, string Url, string? Authorization, string Body)> Requests = new();
    public HttpStatusCode CourseStatus { get; set; } = HttpStatusCode.OK;
    public HttpStatusCode UserIdStatus { get; set; } = HttpStatusCode.OK;
    public object CourseResponse { get; set; } = new { courseId = 987654 };
    public string? CourseErrorBody { get; set; }
    public bool RefreshReturnsNonJson { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((request.Method, request.RequestUri!.GetLeftPart(UriPartial.Path), request.Headers.Authorization?.ToString(), body));
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/oauth/token"))
        {
            var form = QueryHelpers.ParseQuery(body);
            if (form["grant_type"] == "refresh_token" && RefreshReturnsNonJson)
                return new(HttpStatusCode.OK) { Content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json") };
            if (form["grant_type"] == "refresh_token" && form["refresh_token"] == "dead")
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant" }) };
            return Json(new { access_token = $"garmin-access-{form["grant_type"]}", refresh_token = "garmin-refresh", expires_in = 86400, token_type = "bearer" });
        }
        if (path.EndsWith("/user/id")) return UserIdStatus != HttpStatusCode.OK ? new(UserIdStatus) : Json(new { userId = "garmin-user-1" });
        if (path.EndsWith("/user/registration")) return new(HttpStatusCode.NoContent);
        if (path.EndsWith("/course")) return CourseStatus == HttpStatusCode.OK ? Json(CourseResponse)
            : new(CourseStatus) { Content = CourseErrorBody is null ? null : new StringContent(CourseErrorBody, System.Text.Encoding.UTF8, "application/json") };
        return new(HttpStatusCode.NotFound);
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}

public sealed class GarminTests
{
    private readonly StubGarmin garmin = new();
    private ApiFactory App() => new(new(), new() { ["Integrations:Garmin:ClientId"] = "client-1", ["Integrations:Garmin:ClientSecret"] = "secret-1" },
        configure: services =>
        {
            services.AddHttpClient<GarminConnector>().ConfigurePrimaryHttpMessageHandler(() => garmin);
            services.AddHttpClient<GarminCourseDestination>().ConfigurePrimaryHttpMessageHandler(() => garmin);
        });

    private static async Task Connect(HttpClient client)
    {
        using var authorize = await client.GetAsync("/api/connections/garmin/authorize?returnUrl=%2F");
        var location = authorize.Headers.Location!;
        Assert.Equal("https://connect.garmin.com/oauth2Confirm", location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("client-1", query["client_id"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        using var callback = await client.GetAsync($"/api/connections/garmin/callback?code=garmin-code&state={query["state"]}");
        Assert.Equal("/?connected=garmin", callback.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ConnectsWithPkceAndSendsACourse()
    {
        await using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Connect(client);
        var token = garmin.Requests.Single(r => r.Url.EndsWith("/oauth/token"));
        var form = QueryHelpers.ParseQuery(token.Body);
        Assert.Equal("authorization_code", form["grant_type"].ToString());
        Assert.Equal("garmin-code", form["code"].ToString());
        Assert.Equal("secret-1", form["client_secret"].ToString());
        Assert.Matches("^[A-Za-z0-9_-]{43}$", form["code_verifier"].ToString());
        Assert.Equal("http://localhost/api/connections/garmin/callback", form["redirect_uri"].ToString());
        Assert.Equal("Bearer garmin-access-authorization_code", garmin.Requests.Single(r => r.Url.EndsWith("/user/id")).Authorization);

        using var generated = await client.PostAsJsonAsync("/api/routes/generate",
            new { start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 5, activity = "running" });
        var first = (await generated.Content.ReadFromJsonAsync<JsonElement>())[0];
        var id = first.GetProperty("id").GetString();
        using var sent = await client.PostAsync($"/api/routes/{id}/send/garmin", null);
        var result = await sent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("987654", result.GetProperty("externalId").GetString());
        Assert.Equal("https://connect.garmin.com/modern/course/987654", result.GetProperty("viewUrl").GetString());
        var course = garmin.Requests.Single(r => r.Url.EndsWith("/course"));
        Assert.Equal("Bearer garmin-access-authorization_code", course.Authorization);
        var payload = JsonDocument.Parse(course.Body).RootElement;
        Assert.Equal("RUNNING", payload.GetProperty("activityType").GetString());
        Assert.Equal(Math.Round(first.GetProperty("distanceKm").GetDouble() * 1000, 1), payload.GetProperty("distance").GetDouble());
        Assert.True(payload.GetProperty("geoPoints").GetArrayLength() > 2);
    }

    [Fact]
    public async Task SentCoursesLinkBackToMeanderAndAreRememberedForTheSession()
    {
        await using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Connect(client);
        using var generated = await client.PostAsJsonAsync("/api/routes/generate",
            new { start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 5 });
        var first = (await generated.Content.ReadFromJsonAsync<JsonElement>())[0];
        var (id, searchId) = (first.GetProperty("id").GetString(), first.GetProperty("searchId").GetString());
        using var sent = await client.PostAsync($"/api/routes/{id}/send/garmin", null);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var payload = JsonDocument.Parse(garmin.Requests.Single(r => r.Url.EndsWith("/course")).Body).RootElement;
        Assert.Equal($"Meander route: http://localhost/routes/{searchId}?route={id}", payload.GetProperty("description").GetString());

        // Connecting again rotates the session; what was sent moves along with it.
        await Connect(client);
        var remembered = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/routes/sent")).EnumerateArray());
        Assert.Equal(id, remembered.GetProperty("routeId").GetString());
        Assert.Equal("garmin", remembered.GetProperty("service").GetString());
        Assert.Equal("987654", remembered.GetProperty("externalId").GetString());
        Assert.Equal("https://connect.garmin.com/modern/course/987654", remembered.GetProperty("viewUrl").GetString());

        using var stranger = app.CreateClient();
        Assert.Empty((await stranger.GetFromJsonAsync<JsonElement>("/api/routes/sent")).EnumerateArray());

        using var disconnected = await client.DeleteAsync("/api/connections/garmin");
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/routes/sent")).EnumerateArray());
    }

    [Fact]
    public async Task RejectedCourseTokensAskForAReconnect()
    {
        garmin.CourseStatus = HttpStatusCode.Unauthorized;
        await using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Connect(client);
        using var generated = await client.PostAsJsonAsync("/api/routes/generate",
            new { start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 5 });
        var id = (await generated.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetString();
        using var sent = await client.PostAsync($"/api/routes/{id}/send/garmin", null);
        Assert.Equal(HttpStatusCode.Unauthorized, sent.StatusCode);
    }

    [Fact]
    public async Task DeregistrationRemovesTheConnection()
    {
        await using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Connect(client);
        using var hook = await client.PostAsJsonAsync("/api/webhooks/garmin/deregistration",
            new { deregistrations = new[] { new { userId = "garmin-user-1" } } });
        Assert.Equal(HttpStatusCode.OK, hook.StatusCode);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());
    }

    [Theory]
    [InlineData("{", "application/json")]
    [InlineData("{}", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("", "application/json")]
    [InlineData("{\"deregistrations\":[null, 5, {\"userId\": 7}]}", "application/json")]
    [InlineData("{\"deregistrations\":\"x\"}", "application/json")]
    [InlineData("hello", "text/plain")]
    public async Task MalformedDeregistrationsAlwaysGet200AndChangeNothing(string body, string contentType)
    {
        await using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Connect(client);
        using var hook = await client.PostAsync("/api/webhooks/garmin/deregistration", new StringContent(body, System.Text.Encoding.UTF8, contentType));
        Assert.Equal(HttpStatusCode.OK, hook.StatusCode);
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task DeregistrationIsReadRegardlessOfContentType()
    {
        await using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Connect(client);
        using var hook = await client.PostAsync("/api/webhooks/garmin/deregistration",
            new StringContent("{\"deregistrations\":[{\"userId\":\"garmin-user-1\"}]}", System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, hook.StatusCode);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task DisconnectRevokesWithARefreshedToken()
    {
        var clock = new TestClock();
        await using var app = new ApiFactory(new(), new() { ["Integrations:Garmin:ClientId"] = "client-1", ["Integrations:Garmin:ClientSecret"] = "secret-1" },
            configure: services =>
            {
                services.AddHttpClient<GarminConnector>().ConfigurePrimaryHttpMessageHandler(() => garmin);
                services.AddHttpClient<GarminCourseDestination>().ConfigurePrimaryHttpMessageHandler(() => garmin);
                services.AddSingleton<TimeProvider>(clock);
            });
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Connect(client);
        clock.Now += TimeSpan.FromDays(2);
        using var deleted = await client.DeleteAsync("/api/connections/garmin");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Contains(garmin.Requests, r => r.Url.EndsWith("/oauth/token") && QueryHelpers.ParseQuery(r.Body)["grant_type"] == "refresh_token");
        var revoke = garmin.Requests.Single(r => r.Method == HttpMethod.Delete && r.Url.EndsWith("/user/registration"));
        Assert.Equal("Bearer garmin-access-refresh_token", revoke.Authorization);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task DisconnectRemovesTheConnectionWhenTheRefreshReturnsBadJson()
    {
        var clock = new TestClock();
        garmin.RefreshReturnsNonJson = true;
        await using var app = new ApiFactory(new(), new() { ["Integrations:Garmin:ClientId"] = "client-1", ["Integrations:Garmin:ClientSecret"] = "secret-1" },
            configure: services =>
            {
                services.AddHttpClient<GarminConnector>().ConfigurePrimaryHttpMessageHandler(() => garmin);
                services.AddHttpClient<GarminCourseDestination>().ConfigurePrimaryHttpMessageHandler(() => garmin);
                services.AddSingleton<TimeProvider>(clock);
            });
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Connect(client);
        clock.Now += TimeSpan.FromDays(2);
        using var deleted = await client.DeleteAsync("/api/connections/garmin");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Contains(garmin.Requests, r => r.Url.EndsWith("/oauth/token") && QueryHelpers.ParseQuery(r.Body)["grant_type"] == "refresh_token");
        Assert.DoesNotContain(garmin.Requests, r => r.Method == HttpMethod.Delete && r.Url.EndsWith("/user/registration"));
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task ConnectFailsWhenGarminReturnsNoUserId()
    {
        garmin.UserIdStatus = HttpStatusCode.InternalServerError;
        await using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var authorize = await client.GetAsync("/api/connections/garmin/authorize?returnUrl=%2F");
        var state = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["state"];
        using var callback = await client.GetAsync($"/api/connections/garmin/callback?code=garmin-code&state={state}");
        Assert.Equal("/?connectError=garmin", callback.Headers.Location!.OriginalString);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());
        Assert.Contains(garmin.Requests, r => r.Method == HttpMethod.Delete && r.Url.EndsWith("/user/registration")
            && r.Authorization == "Bearer garmin-access-authorization_code");
    }

    private async Task<HttpResponseMessage> SendRoute()
    {
        await using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Connect(client);
        using var generated = await client.PostAsJsonAsync("/api/routes/generate",
            new { start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 5 });
        var id = (await generated.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetString();
        return await client.PostAsync($"/api/routes/{id}/send/garmin", null);
    }

    [Fact]
    public async Task ACourseResponseWithoutACourseIdIsServiceUnavailable()
    {
        garmin.CourseResponse = new { courseId = (string?)null };
        using var sent = await SendRoute();
        // Not 502: Cloudflare replaces origin 502 responses with its own HTML page.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, sent.StatusCode);
        Assert.Equal("serviceUnavailable", (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task CourseFailuresAreNotRetried()
    {
        garmin.CourseStatus = HttpStatusCode.InternalServerError;
        using var sent = await SendRoute();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, sent.StatusCode);
        Assert.Single(garmin.Requests, r => r.Url.EndsWith("/course"));
    }

    [Fact]
    public async Task RejectedCoursesCarryGarminsReasonWithoutTokens()
    {
        garmin.CourseStatus = HttpStatusCode.BadRequest;
        garmin.CourseErrorBody = "{\"errorMessage\":\"activityType WALKING is not supported\"}\n" + new string('x', 1000);
        var destination = new GarminCourseDestination(new HttpClient(garmin), new GarminOptions { ClientId = "c", ClientSecret = "s" });
        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            destination.SendAsync(Fixtures.Candidate(), Activity.Walking, null, new("secret-token", null, DateTimeOffset.UtcNow.AddHours(1), "u"), default));
        Assert.Contains("(400)", failure.Message);
        Assert.Contains("activityType WALKING is not supported", failure.Message);
        Assert.DoesNotContain("secret-token", failure.Message);
        Assert.DoesNotContain("\n", failure.Message);
        Assert.True(failure.Message.Length < 400);
    }

    [Fact]
    public async Task RefreshSendsTheRefreshTokenAndMapsInvalidGrant()
    {
        var options = new GarminOptions { ClientId = "client-1", ClientSecret = "secret-1" };
        var connector = new GarminConnector(new HttpClient(garmin), options, TimeProvider.System);
        var refreshed = await connector.RefreshAsync(new("old", "live", DateTimeOffset.UtcNow, "u"), default);
        Assert.Equal("garmin-access-refresh_token", refreshed.AccessToken);
        Assert.Equal("u", refreshed.ExternalUserId);
        var failure = await Assert.ThrowsAsync<ProviderException>(() => connector.RefreshAsync(new("old", "dead", DateTimeOffset.UtcNow, "u"), default));
        Assert.Equal(ProviderFailure.Unauthorized, failure.Failure);
    }

    [Fact]
    public void MapsRoutesToGarminCourses()
    {
        var course = GarminCourseMapper.Map(Fixtures.Candidate(), Activity.Walking, null);
        Assert.Equal("Meander 2.0 km", course.CourseName);
        Assert.Equal("Planned with Meander", course.Description);
        Assert.Equal("Meander route: https://meander.test/routes/s?route=r",
            GarminCourseMapper.Map(Fixtures.Candidate(), Activity.Walking, new("https://meander.test/routes/s?route=r")).Description);
        Assert.Equal("WALKING", course.ActivityType);
        Assert.Equal("WGS84", course.CoordinateSystem);
        Assert.Equal(2000, course.Distance);
        Assert.Equal(12.5, course.ElevationGain);
        Assert.Equal(12.5, course.ElevationLoss);
        Assert.Equal(new GarminGeoPoint(52.09, 5.12, 12.5), course.GeoPoints[0]);
        Assert.Null(course.GeoPoints[1].Elevation);
    }

    [Fact]
    public void ElevationLossFollowsTheDescentsWhenEveryPointHasAnElevation()
    {
        var points = new[] { 10.0, 30, 20 }.Select((e, i) => Geo.Destination(Fixtures.Start, 90, i * .5) with { ElevationM = e });
        var route = Fixtures.Candidate() with { Geometry = LineString.From(points.ToArray()), ElevationGainM = 20 };
        var course = GarminCourseMapper.Map(route, Activity.Running, null);
        Assert.Equal(20, course.ElevationGain);
        Assert.Equal(10, course.ElevationLoss);
    }

    [Fact]
    public void GarminGetsZeroElevationWhenTheRouteHasNone()
    {
        var course = GarminCourseMapper.Map(Fixtures.Candidate() with { ElevationGainM = null }, Activity.Running, null);
        Assert.Equal(0, course.ElevationGain);
        Assert.Equal(0, course.ElevationLoss);
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("client", "", false)]
    [InlineData("client", "secret", true)]
    public void ValidatesCredentials(string id, string secret, bool valid)
    {
        var options = new GarminOptions { ClientId = id, ClientSecret = secret };
        if (valid) options.Validate(); else Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
