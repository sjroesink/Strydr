using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meander.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Meander.Tests;

public sealed class ConnectionApiTests
{
    private readonly FakeConnector connector = new();
    private readonly FakeDestination destination = new();
    private ApiFactory App(Dictionary<string, string?>? settings = null) => new(new(), settings, configure: services =>
    {
        services.AddSingleton<IServiceConnector>(connector);
        services.AddSingleton<IRouteDestination>(destination);
    });
    private static HttpClient Client(ApiFactory app) => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    private static async Task<string> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    private static async Task<string> Authorize(HttpClient client, string returnUrl = "/routes/s?route=r")
    {
        using var response = await client.GetAsync($"/api/connections/garmin/authorize?returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("provider.test", location.Host);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("http://localhost/api/connections/garmin/callback", query["redirect_uri"].ToString());
        return query["state"].ToString();
    }

    private static async Task<string> RouteId(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/routes/generate",
            new { start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 5, activity = "walking" });
        return (await response.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task ConnectsSendsAndDisconnects()
    {
        await using var app = App();
        using var client = Client(app);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());

        var state = await Authorize(client);
        using var callback = await client.GetAsync($"/api/connections/garmin/callback?code=abc&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/routes/s?route=r&connected=garmin", callback.Headers.Location!.OriginalString);
        var exchange = Assert.Single(connector.Exchanges);
        Assert.Equal("abc", exchange.Code);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", exchange.Verifier);

        var status = (await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0];
        Assert.Equal("garmin", status.GetProperty("service").GetString());
        Assert.True(status.GetProperty("connected").GetBoolean());

        var id = await RouteId(client);
        using var sent = await client.PostAsync($"/api/routes/{id}/send/garmin", null);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("course-1", (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("externalId").GetString());
        var delivered = Assert.Single(destination.Sent);
        Assert.Equal(Activity.Walking, delivered.Activity);
        Assert.Equal(id, delivered.Route.Id.ToString());

        using var removed = await client.DeleteAsync("/api/connections/garmin");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(1, connector.Revokes);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task RejectsAForeignOrReplayedState()
    {
        await using var app = App();
        using var client = Client(app);
        await Authorize(client);
        using var forged = await client.GetAsync("/api/connections/garmin/callback?code=abc&state=forged");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal("connectionStateInvalid", await Code(forged));
        Assert.Empty(connector.Exchanges);

        var state = await Authorize(client);
        (await client.GetAsync($"/api/connections/garmin/callback?code=abc&state={state}")).Dispose();
        using var replay = await client.GetAsync($"/api/connections/garmin/callback?code=abc&state={state}");
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.test/x", "/")]
    [InlineData("//evil.test/x", "/")]
    [InlineData("/\\evil.test", "/")]
    [InlineData("/\t/evil.test", "/")]
    [InlineData("/\n/evil.test", "/")]
    [InlineData("/routes/a?route=b", "/routes/a?route=b")]
    public async Task OnlyReturnsToLocalPaths(string returnUrl, string expected)
    {
        await using var app = App();
        using var client = Client(app);
        var state = await Authorize(client, returnUrl);
        using var callback = await client.GetAsync($"/api/connections/garmin/callback?code=abc&state={state}");
        var separator = expected.Contains('?') ? "&" : "?";
        Assert.Equal($"{expected}{separator}connected=garmin", callback.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/a?b=c", "/a?b=c")]
    [InlineData("//x", "/")]
    [InlineData("/\\x", "/")]
    [InlineData("/\t/x", "/")]
    [InlineData("/a\r\nb", "/")]
    [InlineData("https://x", "/")]
    public void LocalPathKeepsOnlySafeLocalPaths(string? returnUrl, string expected) =>
        Assert.Equal(expected, ConnectionEndpoints.LocalPath(returnUrl));

    // These tests drive the cookies by hand so they can plant, swap and compare session ids.
    private static HttpClient Manual(ApiFactory app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static Dictionary<string, string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0].Split('=', 2)).ToDictionary(p => p[0], p => p[1])
            : [];

    private static async Task<HttpResponseMessage> Get(HttpClient client, string url, params (string Name, string Value)[] cookies)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookies.Length > 0) request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")));
        return await client.SendAsync(request);
    }

    private static async Task<bool> IsConnected(HttpClient client, string sid)
    {
        using var response = await Get(client, "/api/connections", ("meander_sid", sid));
        return (await response.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("connected").GetBoolean();
    }

    // Authorizes with an optional sid cookie, then completes the callback with the sid the browser would hold.
    private static async Task<(string Authorized, string? Final, HttpResponseMessage Callback)> Connect(HttpClient client, string? sid)
    {
        using var authorize = await Get(client, "/api/connections/garmin/authorize",
            sid is null ? [] : [("meander_sid", sid)]);
        var cookies = SetCookies(authorize);
        var authorized = cookies.GetValueOrDefault("meander_sid", sid!);
        var state = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["state"].ToString();
        var callback = await Get(client, $"/api/connections/garmin/callback?code=abc&state={state}",
            ("meander_sid", authorized), ("meander_oauth", cookies["meander_oauth"]));
        return (authorized, SetCookies(callback).GetValueOrDefault("meander_sid"), callback);
    }

    [Fact]
    public async Task ARotatedSessionReplacesAPlantedSessionId()
    {
        await using var app = App();
        using var client = Manual(app);
        var planted = Guid.NewGuid().ToString();
        var (authorized, final, callback) = await Connect(client, planted);
        using var _ = callback;
        Assert.NotEqual(planted, authorized);
        Assert.NotNull(final);
        Assert.NotEqual(planted, final);
        Assert.NotEqual(authorized, final);
        Assert.False(await IsConnected(client, planted));
        Assert.True(await IsConnected(client, final));
    }

    [Fact]
    public async Task RejectsACallbackFromAnotherSession()
    {
        await using var app = App();
        using var client = Manual(app);
        using var authorize = await Get(client, "/api/connections/garmin/authorize");
        var cookies = SetCookies(authorize);
        var state = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["state"].ToString();
        using var callback = await Get(client, $"/api/connections/garmin/callback?code=abc&state={state}",
            ("meander_sid", Guid.NewGuid().ToString()), ("meander_oauth", cookies["meander_oauth"]));
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.Equal("connectionStateInvalid", await Code(callback));
        Assert.Empty(connector.Exchanges);
    }

    [Fact]
    public async Task ConnectingAgainKeepsTheConnectionUnderTheNewSessionOnly()
    {
        await using var app = App();
        using var client = Manual(app);
        var (_, first, firstCallback) = await Connect(client, null);
        firstCallback.Dispose();
        var (authorized, second, secondCallback) = await Connect(client, first);
        secondCallback.Dispose();
        Assert.Equal(first, authorized);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.True(await IsConnected(client, second));
        Assert.False(await IsConnected(client, first!));
    }

    [Fact]
    public async Task DeniedOrFailedConnectionsReturnToTheApp()
    {
        await using var app = App();
        using var client = Client(app);
        var denied = await Authorize(client);
        using var deny = await client.GetAsync($"/api/connections/garmin/callback?error=access_denied&state={denied}");
        Assert.Equal("/routes/s?route=r&connectError=garmin", deny.Headers.Location!.OriginalString);
        var failed = await Authorize(client);
        using var fail = await client.GetAsync($"/api/connections/garmin/callback?code=bad&state={failed}");
        Assert.Equal("/routes/s?route=r&connectError=garmin", fail.Headers.Location!.OriginalString);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task SendingNeedsALiveConnection()
    {
        await using var app = App();
        using var client = Client(app);
        var id = await RouteId(client);
        using var anonymous = await client.PostAsync($"/api/routes/{id}/send/garmin", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("notConnected", await Code(anonymous));

        var state = await Authorize(client);
        (await client.GetAsync($"/api/connections/garmin/callback?code=abc&state={state}")).Dispose();
        destination.Reject = true;
        using var revoked = await client.PostAsync($"/api/routes/{id}/send/garmin", null);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("connectionExpired", await Code(revoked));
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());

        using var missing = await client.PostAsync($"/api/routes/{Guid.NewGuid()}/send/garmin", null);
        Assert.Equal("routeNotFound", await Code(missing));
    }

    [Fact]
    public async Task UnknownOrUnconfiguredServicesAreNotFound()
    {
        connector.IsConfigured = false;
        await using var app = App();
        using var client = Client(app);
        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/connections") ?? []);
        using var garmin = await client.GetAsync("/api/connections/garmin/authorize");
        Assert.Equal("serviceNotFound", await Code(garmin));
        using var unknown = await client.GetAsync("/api/connections/strava/authorize");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AuthorizeMovesOtherHostsToThePublicHostFirst()
    {
        await using var app = App(new() { ["AllowedHosts"] = "*", ["PublicBaseUrl"] = "https://meander.roesink.dev" });
        using var client = Client(app);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/connections/garmin/authorize?returnUrl=%2Froutes%2Fa");
        request.Headers.Host = "meander.sander.ninja";
        using var response = await client.SendAsync(request);
        Assert.Equal("https://meander.roesink.dev/api/connections/garmin/authorize?returnUrl=%2Froutes%2Fa", response.Headers.Location!.ToString());
    }
}
