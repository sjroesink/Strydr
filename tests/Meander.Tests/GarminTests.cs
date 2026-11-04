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
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((request.Method, request.RequestUri!.GetLeftPart(UriPartial.Path), request.Headers.Authorization?.ToString(), body));
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/oauth/token"))
        {
            var form = QueryHelpers.ParseQuery(body);
            if (form["grant_type"] == "refresh_token" && form["refresh_token"] == "dead")
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant" }) };
            return Json(new { access_token = $"garmin-access-{form["grant_type"]}", refresh_token = "garmin-refresh", expires_in = 86400, token_type = "bearer" });
        }
        if (path.EndsWith("/user/id")) return UserIdStatus != HttpStatusCode.OK ? new(UserIdStatus) : Json(new { userId = "garmin-user-1" });
        if (path.EndsWith("/user/registration")) return new(HttpStatusCode.NoContent);
        if (path.EndsWith("/course")) return CourseStatus == HttpStatusCode.OK ? Json(CourseResponse) : new(CourseStatus);
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
    public async Task ACourseResponseWithoutACourseIdIsABadGateway()
    {
        garmin.CourseResponse = new { courseId = (string?)null };
        using var sent = await SendRoute();
        Assert.Equal(HttpStatusCode.BadGateway, sent.StatusCode);
        Assert.Equal("serviceUnavailable", (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task CourseFailuresAreNotRetried()
    {
        garmin.CourseStatus = HttpStatusCode.InternalServerError;
        using var sent = await SendRoute();
        Assert.Equal(HttpStatusCode.BadGateway, sent.StatusCode);
        Assert.Single(garmin.Requests, r => r.Url.EndsWith("/course"));
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
        var course = GarminCourseMapper.Map(Fixtures.Candidate(), Activity.Walking);
        Assert.Equal("Meander 2.0 km", course.CourseName);
        Assert.Equal("WALKING", course.ActivityType);
        Assert.Equal("WGS84", course.CoordinateSystem);
        Assert.Equal(2000, course.Distance);
        Assert.Equal(new GarminGeoPoint(52.09, 5.12, 12.5), course.GeoPoints[0]);
        Assert.Null(course.GeoPoints[1].Elevation);
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
