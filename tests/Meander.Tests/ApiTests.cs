using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Meander.Core;
using Meander.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meander.Tests;

internal sealed class ApiFactory(StubOrs handler, Dictionary<string, string?>? settings = null, IWoodlandProvider? woodlands = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ors:ApiKey"] = "test-key", ["Ors:RetryCount"] = "0", ["Ors:DirectionsPerMinute"] = "1000",
            ["RateLimiting:GeneratePerMinute"] = "100", ["RateLimiting:GeocodePerMinute"] = "100",
            ["ResultStorage:DatabasePath"] = ":memory:"
        }.Concat(settings ?? []).GroupBy(k => k.Key).ToDictionary(g => g.Key, g => g.Last().Value)));
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient<OrsProvider>().ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddSingleton<IWoodlandProvider>(woodlands ?? new FixedWoodland(null));
        });
    }
}

internal sealed class StubOrs : HttpMessageHandler
{
    public HttpStatusCode? FailureStatus { get; set; }
    public int FailFirst { get; set; }
    public bool MissingSnap { get; set; }
    public bool MalformedSnap { get; set; }
    public bool OmitSnapDistance { get; set; }
    public bool NullSnapDistance { get; set; }
    public bool Malformed { get; set; }
    public bool Hang { get; set; }
    public bool IncludeQualities { get; set; }
    public bool IncludeSpur { get; set; }
    public int DelayMs { get; set; }
    public int DirectionCalls;
    public int MaxConcurrency;
    private int active;
    public readonly ConcurrentBag<(string Path, string? Authorization, JsonElement? Body)> Requests = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var concurrent = Interlocked.Increment(ref active);
        int previous;
        do { previous = MaxConcurrency; if (concurrent <= previous) break; }
        while (Interlocked.CompareExchange(ref MaxConcurrency, concurrent, previous) != previous);
        try
        {
            var path = request.RequestUri!.AbsolutePath;
            JsonElement? body = request.Content is null ? null : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            Requests.Add((path, request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null, body));
            if (DelayMs > 0) await Task.Delay(DelayMs, cancellationToken);
            if (path.Contains("/snap/") && MalformedSnap) return Json(new { unexpected = true });
            if (path.Contains("/snap/")) return Json(new { locations = body!.Value.GetProperty("locations").EnumerateArray().Select(point =>
            {
                if (MissingSnap) return null;
                var item = new Dictionary<string, object?> { ["location"] = point.Deserialize<double[]>() };
                if (!OmitSnapDistance) item["snapped_distance"] = NullSnapDistance ? null : 0;
                return item;
            }) });
            if (path.Contains("/geocode/")) return Json(new { features = new[] { new {
                properties = new { gid = "place:utrecht", label = "Utrecht, Netherlands" }, geometry = new { coordinates = new[] { 5.12, 52.09 } }
            } } });
            var count = Interlocked.Increment(ref DirectionCalls);
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (FailureStatus.HasValue || count <= FailFirst)
                return Json(new { error = new { code = 2009, message = "stub upstream failure" } }, FailureStatus ?? HttpStatusCode.TooManyRequests);
            if (Malformed) return Json(new { unexpected = true });
            var coordinates = body!.Value.GetProperty("coordinates").EnumerateArray().Select(p => p.Deserialize<double[]>()!).ToArray();
            var start = new Location(coordinates[0][1], coordinates[0][0]);
            var points = coordinates.Length == 1
                ? new[] { start, Geo.Destination(start, count % 3 * 120, 1.5), Geo.Destination(start, count % 3 * 120 + 60, 1.5), start }
                : coordinates.Select(c => new Location(c[1], c[0])).ToArray();
            if (IncludeSpur && coordinates.Length == 1)
                points = [points[0], points[1], Geo.Destination(points[1], 0, .12), points[1], points[2], points[3]];
            var properties = new Dictionary<string, object> { ["summary"] = new { distance = coordinates.Length == 2 ? 2000 : 5000 }, ["ascent"] = 24.5 };
            if (IncludeQualities)
                properties["extras"] = new {
                    green = new { values = new[] { new[] { 0, points.Length - 1, 8 } } },
                    surface = new { values = new[] { new[] { 0, points.Length - 1, 10 } } }
                };
            return Json(new { type = "FeatureCollection", features = new[] { new {
                type = "Feature", geometry = new { type = "LineString", coordinates = points.Select(p => new[] { p.Longitude, p.Latitude, 10.0 }) },
                properties
            } } });
        }
        finally { Interlocked.Decrement(ref active); }
    }
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
}

public sealed class ApiTests
{
    private static object Request(double distance = 5) => new { start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = distance, activity = "running" };

    [Theory]
    [InlineData(true, "snap", 2)]
    [InlineData(false, "directions", 3)]
    public async Task PersistentInvalidPreviewReportsProviderStageAfterOneRetry(bool snap, string stage, int expectedRequests)
    {
        var upstream = new StubOrs { MalformedSnap = snap, Malformed = !snap };
        await using var app = new ApiFactory(upstream);
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/routes/preview", new {
            start = Fixtures.Start, points = new[] { Geo.Destination(Fixtures.Start, 90, .5) }
        });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(stage, problem.GetProperty("providerStage").GetString());
        Assert.Equal(expectedRequests, upstream.Requests.Count);
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PreviewAcceptsValidSnapCoordinatesWithoutDistanceMetadata(bool missing, bool nullDistance)
    {
        await using var app = new ApiFactory(new() { OmitSnapDistance = missing, NullSnapDistance = nullDistance });
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/routes/preview", new {
            start = Fixtures.Start, points = new[] { Geo.Destination(Fixtures.Start, 0, .5), Geo.Destination(Fixtures.Start, 90, .5), Fixtures.Start }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, preview.GetProperty("snappedPoints").GetArrayLength());
        Assert.Equal(0, preview.GetProperty("start").GetProperty("snapDistanceM").GetDouble());
    }

    [Fact]
    public async Task PreviewCanBeExportedAndItsExactPrefixIsPreservedInCompletedRoutes()
    {
        await using var app = new ApiFactory(new());
        using var client = app.CreateClient();
        var previewResponse = await client.PostAsJsonAsync("/api/routes/preview", new {
            start = new { latitude = 52.09, longitude = 5.12 },
            points = new[] { new { latitude = 52.095, longitude = 5.125 }, new { latitude = 52.1, longitude = 5.13 } }
        });
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var prefix = preview.GetProperty("geometry").GetProperty("coordinates").EnumerateArray().Select(p => p.GetRawText()).ToArray();
        Assert.Equal(3, preview.GetProperty("snappedPoints").GetArrayLength());
        var id = preview.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/routes/{id}/gpx")).StatusCode);
        var generatedResponse = await client.PostAsJsonAsync("/api/routes/generate", new {
            start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 10, drawnRouteId = id
        });
        Assert.Equal(HttpStatusCode.OK, generatedResponse.StatusCode);
        var routes = await generatedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(0, routes.GetArrayLength());
        Assert.All(routes.EnumerateArray(), route =>
        {
            var coordinates = route.GetProperty("geometry").GetProperty("coordinates").EnumerateArray().ToArray();
            Assert.Equal(prefix, coordinates.Take(prefix.Length).Select(p => p.GetRawText()));
            Assert.Equal(5.12, coordinates[^1][0].GetDouble());
            Assert.Equal(52.09, coordinates[^1][1].GetDouble());
        });
    }

    [Fact]
    public async Task InvalidPreviewAndMissingDraftUseProblemDetails()
    {
        await using var app = new ApiFactory(new());
        using var client = app.CreateClient();
        var invalid = await client.PostAsJsonAsync("/api/routes/preview", new { start = new { latitude = 52.09, longitude = 5.12 }, points = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);
        var expired = await client.PostAsJsonAsync("/api/routes/generate", new { start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 5, drawnRouteId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, expired.StatusCode);
        var problem = await expired.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("expired", problem.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(0, 422)]
    [InlineData(10, 200)]
    public async Task FlexiblePointToPointRequestAcceptsShortestRouteWithinItsRange(double margin, int expectedStatus)
    {
        await using var app = new ApiFactory(new());
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/routes/generate", new {
            start = new { latitude = 52.09, longitude = 5.12 }, end = new { latitude = 52.1, longitude = 5.13 },
            targetDistanceKm = 1.9, distanceFlexibilityPercent = margin, preferGreen = true
        });
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        if (expectedStatus == 200)
        {
            var routes = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("qualified", routes[0].GetProperty("status").GetString());
            Assert.Equal(2, routes[0].GetProperty("distanceKm").GetDouble());
        }
    }

    [Fact]
    public async Task PreferencesReachOrsAndSurfaceAndGreenSpansReachCandidates()
    {
        var upstream = new StubOrs { IncludeQualities = true };
        await using var app = new ApiFactory(upstream);
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/routes/generate", new {
            start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 5,
            preferGreen = true, preferUnpaved = true
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var routes = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.All(routes.EnumerateArray(), r =>
        {
            Assert.InRange(r.GetProperty("greenScore").GetDouble(), .799, .801);
            Assert.InRange(r.GetProperty("unpavedPercent").GetDouble(), 99.99, 100.01);
            Assert.InRange(r.GetProperty("surfaceKnownPercent").GetDouble(), 99.99, 100.01);
        });
        Assert.All(upstream.Requests.Where(r => r.Path.Contains("/directions/")), r =>
        {
            var body = r.Body!.Value;
            Assert.Equal(1, body.GetProperty("options").GetProperty("profile_params").GetProperty("weightings").GetProperty("green").GetInt32());
            Assert.Equal(new[] { "green", "surface" }, body.GetProperty("extra_info").EnumerateArray().Select(v => v.GetString()));
        });
    }

    [Fact]
    public async Task BacktrackingCleanupIsUsedByMapMetricsAndGpx()
    {
        await using var app = new ApiFactory(new() { IncludeSpur = true, IncludeQualities = true });
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/routes/generate", Request());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var routes = await response.Content.ReadFromJsonAsync<JsonElement>();
        var route = routes[0];
        Assert.True(route.GetProperty("distanceKm").GetDouble() < 5);
        Assert.Equal(4, route.GetProperty("geometry").GetProperty("coordinates").GetArrayLength());
        Assert.Contains(route.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == "backtrackingRemoved");
        Assert.InRange(route.GetProperty("greenScore").GetDouble(), .799, .801);
        var xml = XDocument.Parse(await client.GetStringAsync($"/api/routes/{route.GetProperty("id").GetString()}/gpx"));
        Assert.Equal(4, xml.Descendants(XName.Get("trkpt", "http://www.topografix.com/GPX/1/1")).Count());
    }

    [Fact]
    public async Task RawOrsKeyWithBase64PaddingIsSentWithoutAuthenticationSchemeParsing()
    {
        const string key = "test/key+with=padding==";
        var upstream = new StubOrs();
        await using var app = new ApiFactory(upstream, new() { ["Ors:ApiKey"] = key });
        using var client = app.CreateClient();
        var response = await client.GetAsync("/api/geocode?q=Utrecht");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(key, Assert.Single(upstream.Requests).Authorization);
    }

    [Fact]
    public async Task RealAdapterGeneratesThreeCandidatesAndExportsGpx()
    {
        var upstream = new StubOrs();
        await using var app = new ApiFactory(upstream);
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/routes/generate", Request());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var routes = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        Assert.Equal(3, routes.Length);
        Assert.All(routes, route =>
        {
            Assert.Equal("qualified", route.GetProperty("status").GetString());
            Assert.Equal("LineString", route.GetProperty("geometry").GetProperty("type").GetString());
            Assert.Equal(24.5, route.GetProperty("elevationGainM").GetDouble());
        });
        var id = routes[0].GetProperty("id").GetString();
        using var gpx = await client.GetAsync($"/api/routes/{id}/gpx");
        Assert.Equal("application/gpx+xml", gpx.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", gpx.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("no-store", gpx.Headers.CacheControl!.ToString());
        var document = XDocument.Parse(await gpx.Content.ReadAsStringAsync());
        Assert.Equal(4, document.Descendants(XName.Get("trkpt", "http://www.topografix.com/GPX/1/1")).Count());
        Assert.Equal(10, upstream.DirectionCalls);
        Assert.All(upstream.Requests, request => Assert.Equal("test-key", request.Authorization));
        Assert.Contains(upstream.Requests, request => request.Path.Contains("/foot-walking/") && request.Body!.Value.TryGetProperty("options", out _));
    }

    [Fact]
    public async Task GeocodingReturnsNormalizedSuggestionsAndEscapesQuery()
    {
        await using var app = new ApiFactory(new());
        using var client = app.CreateClient();
        var results = await client.GetFromJsonAsync<JsonElement>("/api/geocode?q=Utrecht%20%26%20park");
        Assert.Equal("Utrecht, Netherlands", results[0].GetProperty("label").GetString());
        Assert.Equal(52.09, results[0].GetProperty("location").GetProperty("latitude").GetDouble());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/geocode?q=a")).StatusCode);
    }

    [Theory] [InlineData(.4)] [InlineData(61)]
    public async Task ValidationUsesProblemDetails(double distance)
    {
        await using var app = new ApiFactory(new());
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/routes/generate", Request(distance));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("traceId", out _));
        Assert.True(body.GetProperty("errors").TryGetProperty("targetDistanceKm", out _));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"start\":null,\"activity\":17}")]
    [InlineData("{\"start\":{\"latitude\":52},\"targetDistanceKm\":5}")]
    [InlineData("{\"start\":{\"longitude\":5},\"targetDistanceKm\":5}")]
    public async Task MalformedJsonIncompleteCoordinatesAndNumericEnumsReturnProblemDetails(string json)
    {
        await using var app = new ApiFactory(new());
        using var client = app.CreateClient();
        using var response = await client.PostAsync("/api/routes/generate", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task ShortestRouteErrorIncludesMinimumDistance()
    {
        await using var app = new ApiFactory(new());
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/routes/generate", new {
            start = new { latitude = 52.09, longitude = 5.12 }, end = new { latitude = 52.1, longitude = 5.13 }, targetDistanceKm = 1
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, error.GetProperty("minimumDistanceKm").GetDouble());
    }

    [Theory] [InlineData(429, 503)] [InlineData(500, 503)] [InlineData(400, 422)]
    public async Task UpstreamFailuresHaveExplicitStatusCodes(int upstreamStatus, int expected)
    {
        await using var app = new ApiFactory(new() { FailureStatus = (HttpStatusCode)upstreamStatus });
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/routes/generate", Request());
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        if (upstreamStatus == 429) Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task RetriesTransientFailureThenSucceeds()
    {
        var upstream = new StubOrs { FailFirst = 1 };
        await using var app = new ApiFactory(upstream, new() { ["Ors:RetryCount"] = "2" });
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/routes/generate", Request());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(11, upstream.DirectionCalls);
    }

    [Fact]
    public async Task ExhaustsRetryBudgetBeforeReturningFailure()
    {
        var upstream = new StubOrs { FailureStatus = HttpStatusCode.TooManyRequests };
        await using var app = new ApiFactory(upstream, new() { ["Ors:RetryCount"] = "1" });
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/routes/generate", Request())).StatusCode);
        Assert.Equal(20, upstream.DirectionCalls);
    }

    [Fact]
    public async Task OneFailedAttemptReturnsPartialResultsWithWarning()
    {
        var upstream = new StubOrs { FailFirst = 1 };
        await using var app = new ApiFactory(upstream);
        using var client = app.CreateClient();
        var routes = await (await client.PostAsJsonAsync("/api/routes/generate", Request())).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(routes[0].GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == "partialSearch");
    }

    [Fact]
    public async Task ProviderConcurrencyIsSharedAcrossGenerations()
    {
        var upstream = new StubOrs { DelayMs = 20 };
        await using var app = new ApiFactory(upstream);
        using var client = app.CreateClient();
        var responses = await Task.WhenAll(client.PostAsJsonAsync("/api/routes/generate", Request()), client.PostAsJsonAsync("/api/routes/generate", Request()));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.InRange(upstream.MaxConcurrency, 2, 3);
    }

    [Fact]
    public async Task TimeoutReturns504AndMalformedProviderDataReturns502()
    {
        await using var slowApp = new ApiFactory(new() { Hang = true }, new() { ["Generation:DeadlineSeconds"] = "1" });
        using var slowClient = slowApp.CreateClient();
        Assert.Equal(HttpStatusCode.GatewayTimeout, (await slowClient.PostAsJsonAsync("/api/routes/generate", Request())).StatusCode);
        await using var badApp = new ApiFactory(new() { Malformed = true });
        using var badClient = badApp.CreateClient();
        Assert.Equal(HttpStatusCode.BadGateway, (await badClient.PostAsJsonAsync("/api/routes/generate", Request())).StatusCode);
    }

    [Fact]
    public async Task ApplicationRateLimitReturns429WithRetryAfter()
    {
        await using var app = new ApiFactory(new(), new() { ["RateLimiting:GeneratePerMinute"] = "1" });
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/routes/generate", Request())).StatusCode);
        var response = await client.PostAsJsonAsync("/api/routes/generate", Request());
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task MissingExportAndUnknownApiAreProblemDetails()
    {
        await using var app = new ApiFactory(new());
        using var client = app.CreateClient();
        foreach (var path in new[] { $"/api/routes/{Guid.NewGuid()}/gpx", $"/api/searches/{Guid.NewGuid()}", "/api/unknown" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        }
    }
}
