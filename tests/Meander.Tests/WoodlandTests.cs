using System.Net;
using System.Text.Json;
using Meander.Core;
using Meander.Infrastructure;

namespace Meander.Tests;

public sealed class WoodlandTests
{
    public static WoodlandMap ForestNearStart()
    {
        Location At(double east, double north) => Geo.Destination(Geo.Destination(Fixtures.Start, 90, east), 0, north);
        Location[] ring = [At(.5, -.7), At(2.5, -.7), At(2.5, .7), At(.5, .7), At(.5, -.7)];
        var paths = Enumerable.Range(0, 9).SelectMany(x => Enumerable.Range(0, 7)
            .Select(y => At(.55 + x * .2, -.6 + y * .2))).ToArray();
        return new(Fixtures.Start, [new(ring, [])], paths);
    }

    [Fact]
    public void WoodlandMeasureIsLengthWeightedAndExcludesClearings()
    {
        // A line traverses 2 degrees, with 1 degree of mapped forest minus a .2 degree hole.
        Location[] shell = [new(-1, 0), new(-1, 1), new(1, 1), new(1, 0), new(-1, 0)];
        Location[] hole = [new(-.5, .4), new(-.5, .6), new(.5, .6), new(.5, .4), new(-.5, .4)];
        var map = new WoodlandMap(new(0, 0), [new(shell, [hole])], []);
        Assert.True(map.Contains(new(0, .2)));
        Assert.False(map.Contains(new(0, .5)));
        Assert.False(map.Contains(new(0, 1.2)));
        var measure = map.Measure([new(0, -.5), new(0, .05), new(0, .1), new(0, .2), new(0, 1.5)]);
        Assert.InRange(measure.Percent, 39.9, 40.1);
        Assert.InRange(measure.FirstEntryKm!.Value, 55.5, 55.7);
    }

    [Fact]
    public void MultipolygonFragmentsAreStitchedAndInnerRingsRemainHoles()
    {
        using var json = JsonDocument.Parse("""
            {"elements":[{"type":"relation","tags":{"type":"multipolygon","natural":"wood"},"members":[
              {"type":"way","role":"outer","geometry":[{"lat":0,"lon":0},{"lat":0,"lon":2},{"lat":2,"lon":2}]},
              {"type":"way","role":"outer","geometry":[{"lat":0,"lon":0},{"lat":2,"lon":0},{"lat":2,"lon":2}]},
              {"type":"way","role":"inner","geometry":[{"lat":0.5,"lon":0.5},{"lat":1.5,"lon":0.5},{"lat":1.5,"lon":1.5},{"lat":0.5,"lon":1.5},{"lat":0.5,"lon":0.5}]}
            ]}]}
            """);
        var map = OverpassWoodlandProvider.Parse(new(0, 0), json.RootElement);
        Assert.Equal(1, map.PolygonCount);
        Assert.True(map.Contains(new(.2, .2)));
        Assert.False(map.Contains(new(1, 1)));
    }

    [Fact]
    public void TimeoutRemarkIsRejectedRatherThanPresentingPartialForestCoverage()
    {
        using var json = JsonDocument.Parse("""{"remark":"runtime error: query timed out","elements":[]}""");
        Assert.Throws<JsonException>(() => OverpassWoodlandProvider.Parse(Fixtures.Start, json.RootElement));
    }

    [Fact]
    public void ForestTargetsUseOnlyWoodlandPathsInsideTheRequestedRadius()
    {
        var map = ForestNearStart();
        var targets = map.Targets(Fixtures.Start, Fixtures.Start, 8, Fixtures.Start, 1.2);
        Assert.NotEmpty(targets);
        Assert.All(targets.SelectMany(t => t.Points), point =>
        {
            Assert.True(map.Contains(point));
            Assert.True(Geo.DistanceKm(Fixtures.Start, point) <= 1.2);
        });
        Assert.Empty(map.Targets(Fixtures.Start, Fixtures.Start, .6, Fixtures.Start, null));
    }

    // A forest block east of the start between eastFrom and eastTo km, 1 km tall, with a path grid.
    public static WoodlandPolygon ForestBlock(double eastFrom, double eastTo, out Location[] paths)
    {
        Location At(double east, double north) => Geo.Destination(Geo.Destination(Fixtures.Start, 90, east), 0, north);
        var columns = (int)Math.Round((eastTo - eastFrom) / .2);
        paths = Enumerable.Range(0, columns).SelectMany(x => Enumerable.Range(0, 5)
            .Select(y => At(eastFrom + .1 + x * .2, -.4 + y * .2))).ToArray();
        return new([At(eastFrom, -.5), At(eastTo, -.5), At(eastTo, .5), At(eastFrom, .5), At(eastFrom, -.5)], []);
    }

    [Fact]
    public void AForestPathBetweenTwoAdjoiningForestPolygonsCountsAsWoodland()
    {
        // Imported forest is often cut along its paths, leaving the path in a narrow gap.
        Location At(double east, double north) => Geo.Destination(Geo.Destination(Fixtures.Start, 90, east), 0, north);
        WoodlandPolygon Block(double from, double to) => new([At(from, -.5), At(to, -.5), At(to, .5), At(from, .5), At(from, -.5)], []);
        var map = new WoodlandMap(Fixtures.Start, [Block(0, .995), Block(1.005, 2)], [At(1, 0)]);
        Assert.True(map.Contains(At(1, 0)));
        Assert.InRange(map.Measure([At(1, -.4), At(1, .4)]).Percent, 99, 100);
        Assert.False(map.Contains(At(1, .6)));
        Assert.False(map.Contains(At(2.1, 0)));
    }

    [Fact]
    public void WoodlandLoopsStringSeparateForestBlocksAndRankByForestAlongThem()
    {
        // Two blocks east of the start, too far apart for one cluster, but both on a long loop.
        var near = ForestBlock(.4, 1.6, out var nearPaths);
        var far = ForestBlock(3, 4.2, out var farPaths);
        var map = new WoodlandMap(Fixtures.Start, [near, far], [.. nearPaths, .. farPaths]);
        var loops = map.Loops(Fixtures.Start, 12, Fixtures.Start, null);
        Assert.NotEmpty(loops);
        Assert.InRange(loops[0].Bearing, 45, 135);
        var waypoints = loops.Select(loop => loop.Waypoints(12, 1, 1, point => point)).ToArray();
        Assert.All(waypoints, points =>
        {
            Assert.Equal(Fixtures.Start, points[0]);
            Assert.Equal(Fixtures.Start, points[^1]);
            Assert.All(points.Skip(1).SkipLast(1), p => Assert.True(map.Contains(p)));
        });
        Assert.Contains(waypoints, points => points.Any(p => Geo.DistanceKm(Fixtures.Start, p) < 1.7)
            && points.Any(p => Geo.DistanceKm(Fixtures.Start, p) > 3));
        var cover = waypoints.Select(points => map.Measure(points).Percent * points.Zip(points.Skip(1), Geo.DistanceKm).Sum()).ToArray();
        Assert.Equal(cover.Max(), cover[0]);
    }

    [Fact]
    public void WoodlandLoopsFromADrawnPointStayOnTheSideAwayFromHome()
    {
        var midway = ForestBlock(.6, 1.4, out var midwayPaths);
        var beyond = ForestBlock(3.2, 4.2, out var beyondPaths);
        var map = new WoodlandMap(Fixtures.Start, [midway, beyond], [.. midwayPaths, .. beyondPaths]);
        var focus = Geo.Destination(Fixtures.Start, 90, 3);
        var loops = map.Loops(focus, 4, Fixtures.Start, null, home: Fixtures.Start);
        Assert.NotEmpty(loops);
        Assert.All(loops, loop =>
        {
            Assert.InRange(loop.Bearing, 0, 180);
            var points = loop.Waypoints(4, 1, 1, point => point, finish: Fixtures.Start);
            Assert.Equal(Fixtures.Start, points[^1]);
            Assert.All(points.Skip(1).SkipLast(1), p => Assert.True(Geo.DistanceKm(Fixtures.Start, p) > 3.1));
        });
    }

    [Fact]
    public async Task ForestPreferenceDirectsSearchIntoWoodlandAndOutranksHighUrbanGreenIndex()
    {
        var map = ForestNearStart();
        var woodland = new FixedWoodland(map);
        var provider = new FakeRouting
        {
            Round = (_, _) => Task.FromResult(Fixtures.Petal(220, distance: 8) with {
                SegmentQualities = [new(1, false), new(1, false), new(1, false)] }),
            Via = (query, _) => Task.FromResult(new RouteResult(query.Points, 8.2, 0,
                SegmentQualities: Enumerable.Repeat(new SegmentQuality(.1, true), query.Points.Count - 1).ToArray()))
        };
        var routes = await new RouteGenerator(provider, new MemoryStore(), new(), new TestClock(), woodland)
            .GenerateAsync(Fixtures.Loop(8) with { PreferGreen = true, PreferUnpaved = true, DistanceFlexibilityPercent = 15 }, default);
        Assert.Equal(1, woodland.Calls);
        Assert.Equal(2, provider.Rounds);
        Assert.Equal(8, provider.ViaQueries.Count);
        Assert.True(routes[0].WoodlandPercent > 20);
        Assert.Equal(.1, routes[0].GreenScore!.Value, precision: 5);
        Assert.All(provider.ViaQueries, query => Assert.All(query.Points.Skip(1).SkipLast(1), point => Assert.True(map.Contains(point))));
        Assert.All(routes, route => Assert.InRange(route.WoodlandPercent!.Value, 0, 100));
    }

    [Fact]
    public async Task PointToPointKeepsShortestValidationAndItsNineCallBudget()
    {
        var provider = new FakeRouting();
        var forest = new FixedWoodland(ForestNearStart());
        var end = Geo.Destination(Fixtures.Start, 90, .8);
        var generator = new RouteGenerator(provider, new MemoryStore(), new(), new TestClock(), forest);
        await generator.GenerateAsync(Fixtures.Loop(5) with { End = end, PreferGreen = true }, default);
        Assert.InRange(provider.ViaQueries.Count, 2, 9);
        Assert.True(provider.ViaQueries[0].Shortest);
        provider.ViaQueries.Clear();
        var error = await Assert.ThrowsAsync<RouteValidationException>(() => generator.GenerateAsync(
            Fixtures.Loop(1) with { End = end, PreferGreen = true }, default));
        Assert.Equal(2, error.MinimumDistanceKm);
        Assert.Single(provider.ViaQueries);
    }

    [Fact]
    public async Task NoGreenPreferenceSkipsWoodlandLookup()
    {
        var source = new FixedWoodland(ForestNearStart());
        var routes = await new RouteGenerator(new FakeRouting(), new MemoryStore(), new(), new TestClock(), source)
            .GenerateAsync(Fixtures.Loop(), default);
        Assert.Equal(0, source.Calls);
        Assert.All(routes, route => Assert.Null(route.WoodlandPercent));
    }

    [Fact]
    public async Task UnavailableWoodlandReturnsUsableRoutesWithAnHonestWarning()
    {
        var routes = await new RouteGenerator(new FakeRouting(), new MemoryStore(), new(), new TestClock(), new FixedWoodland(null))
            .GenerateAsync(Fixtures.Loop() with { PreferGreen = true }, default);
        Assert.NotEmpty(routes);
        Assert.All(routes, route =>
        {
            Assert.Null(route.WoodlandPercent);
            Assert.Contains(route.Warnings, w => w.Code == "woodlandUnavailable");
        });
    }

    [Theory]
    [InlineData(200)]
    [InlineData(429)]
    public async Task AreaLookupIsCachedAndConcurrentRequestsShareOneUpstreamCall(int status)
    {
        var handler = new WoodlandHandler((HttpStatusCode)status);
        using var client = new HttpClient(handler) { MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        using var source = new OverpassWoodlandProvider(new Clients(client), new(), new TestClock());
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => source.FindAsync(Fixtures.Start, 3, default)));
        Assert.Equal(1, handler.Calls);
        Assert.All(results, map => Assert.Equal(status == 200, map is not null));
        Assert.DoesNotContain("Authorization", handler.Headers!);
        Assert.Contains("natural=wood", handler.Body!);
        Assert.Contains("landuse=forest", handler.Body!);
    }

    private sealed class Clients(HttpClient client) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => client; }
    private sealed class WoodlandHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls;
        public string? Headers;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Headers = request.Headers.ToString();
            Body = Uri.UnescapeDataString(await request.Content!.ReadAsStringAsync(cancellationToken));
            await Task.Delay(10, cancellationToken);
            return new(status) { Content = new StringContent("""{"elements":[]}""") };
        }
    }
}
