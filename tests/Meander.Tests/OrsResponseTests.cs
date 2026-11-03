using System.Net;
using System.Text.Json;
using Meander.Core;
using Meander.Infrastructure;

namespace Meander.Tests;

public sealed class OrsResponseTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"unknown\"")]
    [InlineData("-1")]
    public async Task SnapDistanceUsesCoordinatesRegardlessOfOptionalMetadata(string? metadata)
    {
        var location = Geo.Destination(Fixtures.Start, 90, .3);
        var item = new Dictionary<string, object?> { ["location"] = new[] { location.Longitude, location.Latitude } };
        if (metadata is not null) item["snapped_distance"] = JsonSerializer.Deserialize<JsonElement>(metadata);
        using var client = Client(JsonSerializer.Serialize(new { locations = new[] { item } }));
        var points = await new OrsProvider(client, new()).SnapAsync([Fixtures.Start], Activity.Running, 1000, default);
        Assert.InRange(Assert.Single(points)!.DistanceM, 299.9, 300.1);
    }

    [Theory]
    [InlineData("{\"green\":{\"values\":[[0,900,8]]}}")]
    [InlineData("{\"surface\":{\"values\":[[0,3,\"unknown\"]]}}")]
    [InlineData("{\"surface\":{\"values\":null}}")]
    public async Task InvalidOptionalQualitiesKeepTheValidGeometry(string extras)
    {
        using var client = Client(Body(extras));
        var route = await new OrsProvider(client, new()).RoundTripAsync(new(Fixtures.Start, 5, 3, 1, Activity.Running, new(true, true)), default);
        Assert.Equal(4, route.Geometry.Count);
        Assert.Equal(5, route.DistanceKm);
        Assert.Null(route.ElevationGainM);
        Assert.Null(route.Geometry[0].ElevationM);
        Assert.Null(route.SegmentQualities);
        Assert.Contains(route.Warnings!, w => w.Code == "qualityDataUnavailable");
    }

    [Fact]
    public async Task InvalidMandatoryGeometryStillFailsWithProviderStage()
    {
        using var client = Client(Body("{}").Replace("LineString", "Point"));
        var error = await Assert.ThrowsAsync<ProviderException>(() => new OrsProvider(client, new())
            .RoundTripAsync(new(Fixtures.Start, 5, 3, 1, Activity.Running), default));
        Assert.Equal(ProviderFailure.InvalidResponse, error.Failure);
        Assert.Equal("directions", error.ResponseSection);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreviewRetriesOneInvalidResponseForSnapOrDirections(bool failSnap)
    {
        var provider = new FakeRouting();
        var calls = 0;
        if (failSnap) provider.Snap = points => ++calls == 1 ? throw new ProviderException(ProviderFailure.InvalidResponse, "Temporary invalid response")
            : points.Select(p => (SnapResult?)new(p, 0)).ToArray();
        else provider.Via = (q, _) => ++calls == 1 ? throw new ProviderException(ProviderFailure.InvalidResponse, "Temporary invalid response")
            : Task.FromResult(new RouteResult(q.Points, 1, 0));
        var route = await new RouteGenerator(provider, new MemoryStore(), new(), new TestClock())
            .PreviewAsync(new(Fixtures.Start, [Geo.Destination(Fixtures.Start, 90, .5)]), default);
        Assert.Equal(2, calls);
        Assert.NotEmpty(route.Geometry.Coordinates);
    }

    [Theory]
    [InlineData(ProviderFailure.InvalidResponse, 2)]
    [InlineData(ProviderFailure.NoRoute, 1)]
    public async Task PreviewRetryIsBoundedAndNoRouteIsNotRetried(ProviderFailure failure, int expected)
    {
        var calls = 0;
        var provider = new FakeRouting { Via = (_, _) => { calls++; throw new ProviderException(failure, "Unavailable route"); } };
        await Assert.ThrowsAsync<ProviderException>(() => new RouteGenerator(provider, new MemoryStore(), new(), new TestClock())
            .PreviewAsync(new(Fixtures.Start, [Geo.Destination(Fixtures.Start, 90, .5)]), default));
        Assert.Equal(expected, calls);
    }

    private static string Body(string extras) => """
        {"features":[{"geometry":{"type":"LineString","coordinates":[[5.12,52.09,null],[5.13,52.1,10],[5.14,52.09,10],[5.12,52.09,10]]},
        "properties":{"summary":{"distance":5000,"ascent":null},"warnings":[{"message":null}],"extras":EXTRAS}}]}
        """.Replace("EXTRAS", extras);
    private static HttpClient Client(string body) => new(new Reply(body)) { BaseAddress = new("https://ors.invalid/") };
    private sealed class Reply(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }
}
