using Meander.Core;
using Meander.Infrastructure;

namespace Meander.Tests;

public sealed class DrawingTests
{
    private readonly FakeRouting provider = new();
    private readonly MemoryStore store = new();
    private readonly TestClock clock = new();
    private RouteGenerator Generator() => new(provider, store, new(), clock);
    private static Location Point(double bearing, double distance = 1) => Geo.Destination(Fixtures.Start, bearing, distance);

    [Fact]
    public async Task PreviewSnapsOrderedPointsCachesItsGeometryAndCanExportDirectly()
    {
        var points = new[] { Point(0), Point(90) };
        var preview = await Generator().PreviewAsync(new(Fixtures.Start, points), default);
        Assert.Equal(5, preview.DistanceKm);
        Assert.Equal(3, preview.Geometry.Coordinates.Length);
        Assert.Equal(points, Assert.Single(provider.ViaQueries).Points.Skip(1));
        Assert.Equal(preview, store.Get(preview.Id));
        Assert.NotNull(store.GetDraft(preview.Id));
        Assert.Contains("trkpt", System.Text.Encoding.UTF8.GetString(GpxWriter.Write(preview)));
    }

    [Fact]
    public async Task CompletionPreservesEveryFixedCoordinateEvenAnIntentionalOutAndBack()
    {
        var generator = Generator();
        var preview = await generator.PreviewAsync(new(Fixtures.Start, [Point(0), Fixtures.Start, Point(90)]), default);
        var fixedCoordinates = preview.Geometry.Coordinates;
        var routes = await generator.GenerateAsync(Fixtures.Loop(10) with { DrawnRouteId = preview.Id }, default);
        Assert.NotEmpty(routes);
        Assert.All(routes, route =>
        {
            Assert.Equal(fixedCoordinates.Length, route.Geometry.Coordinates.Take(fixedCoordinates.Length).Count());
            for (var i = 0; i < fixedCoordinates.Length; i++) Assert.Equal(fixedCoordinates[i], route.Geometry.Coordinates[i]);
            Assert.Equal(Fixtures.Start, route.Start.Requested);
            Assert.Equal(Fixtures.Start, route.End.Requested);
            Assert.True(route.DistanceKm >= preview.DistanceKm);
        });
        Assert.Equal(1, provider.ViaQueries.Count(q => q.Points[0] == Fixtures.Start));
    }

    [Fact]
    public async Task ShortTargetReportsPrefixPlusMinimumConnectionDistance()
    {
        var generator = Generator();
        var preview = await generator.PreviewAsync(new(Fixtures.Start, [Point(90)]), default);
        var error = await Assert.ThrowsAsync<RouteValidationException>(() => generator.GenerateAsync(
            Fixtures.Loop(4) with { DrawnRouteId = preview.Id }, default));
        Assert.Equal(7, error.MinimumDistanceKm);
        Assert.Contains("drawn section", error.Message);
    }

    [Fact]
    public async Task AlreadyCompletedDrawnLoopIsReturnedWithoutAnotherDirectionsCall()
    {
        var generator = Generator();
        var preview = await generator.PreviewAsync(new(Fixtures.Start, [Point(0), Point(90), Fixtures.Start]), default);
        var route = Assert.Single(await generator.GenerateAsync(Fixtures.Loop(5) with { DrawnRouteId = preview.Id }, default));
        Assert.Single(provider.ViaQueries);
        Assert.Equal(0, provider.Rounds);
        Assert.Equal(preview.DistanceKm, route.DistanceKm);
        Assert.Equal(preview.Geometry.Coordinates.Length, route.Geometry.Coordinates.Length);
    }

    [Fact]
    public async Task ExtendingAClosedDrawnLoopRetainsItBeforeTheAdditionalLoop()
    {
        var generator = Generator();
        var preview = await generator.PreviewAsync(new(Fixtures.Start, [Point(0), Point(90), Fixtures.Start]), default);
        var routes = await generator.GenerateAsync(Fixtures.Loop(10) with { DrawnRouteId = preview.Id }, default);
        Assert.Contains(routes, r => r.Status == CandidateStatus.Qualified);
        Assert.Equal(10, provider.Rounds);
        Assert.All(routes, route => Assert.Equal(preview.Geometry.Coordinates[1], route.Geometry.Coordinates[1]));
    }

    // Continuation queries (after the preview) that loop beyond the drawn point, away from home.
    private IReadOnlyList<ViaPointsQuery> LoopsBeyond(Location drawn) => provider.ViaQueries.Skip(1)
        .Where(q => !q.Shortest && q.Points.Skip(1).SkipLast(1).All(p => p.Longitude > drawn.Longitude)).ToArray();

    [Fact]
    public async Task GreenDrawnPointExploresTheForestAroundItInsteadOfDetouringHome()
    {
        // A cheaper forest on the direct way home must not pull the search away from the drawn point.
        var midway = WoodlandTests.ForestBlock(.6, 1.4, out var midwayPaths);
        var beyond = WoodlandTests.ForestBlock(3.2, 4.2, out var beyondPaths);
        var map = new WoodlandMap(Fixtures.Start, [midway, beyond], [.. midwayPaths, .. beyondPaths]);
        var generator = new RouteGenerator(provider, store, new(), clock, new FixedWoodland(map));
        var drawn = Point(90, 3);
        var preview = await generator.PreviewAsync(new(Fixtures.Start, [drawn], PreferGreen: true, PreferUnpaved: true), default);
        var routes = await generator.GenerateAsync(Fixtures.Loop(14) with { DrawnRouteId = preview.Id, PreferGreen = true, PreferUnpaved = true }, default);
        Assert.InRange(provider.ViaQueries.Count - 1, 2, 9);
        // Forest ovals consist of woodland path points beyond the drawn point, then head home.
        var forestQueries = provider.ViaQueries.Skip(1).Where(q => q.Points.Count > 2 && q.Points.Skip(1).SkipLast(1).All(map.Contains)).ToArray();
        Assert.InRange(forestQueries.Length, 3, 6);
        Assert.All(forestQueries, q =>
        {
            Assert.Equal(drawn, q.Points[0]);
            Assert.Equal(Fixtures.Start, q.Points[^1]);
            Assert.All(q.Points.Skip(1).SkipLast(1), p => Assert.True(Geo.DistanceKm(Fixtures.Start, p) > 3.1));
        });
        // No attempt is drawn into the forest on the direct way home.
        Assert.DoesNotContain(provider.ViaQueries.Skip(1), q => q.Points.Skip(1).SkipLast(1)
            .Any(p => map.Contains(p) && Geo.DistanceKm(Fixtures.Start, p) < 1.6));
        Assert.True(routes[0].WoodlandPercent > 0);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WithoutWoodlandDataADrawnPointStillGetsLoopsAroundIt(bool green, bool unpaved)
    {
        var generator = new RouteGenerator(provider, store, new(), clock, new FixedWoodland(null));
        var drawn = Point(90, 3);
        var preview = await generator.PreviewAsync(new(Fixtures.Start, [drawn], PreferGreen: green, PreferUnpaved: unpaved), default);
        var routes = await generator.GenerateAsync(Fixtures.Loop(14) with { DrawnRouteId = preview.Id, PreferGreen = green, PreferUnpaved = unpaved }, default);
        Assert.NotEmpty(routes);
        Assert.InRange(provider.ViaQueries.Count - 1, 2, 9);
        Assert.InRange(LoopsBeyond(drawn).Count, 2, 6);
        // The best candidate stays near the drawn point rather than detouring elsewhere.
        Assert.Contains(routes[0].Geometry.Coordinates.Skip(preview.Geometry.Coordinates.Length), c => c[0] > drawn.Longitude);
    }

    [Theory]
    [InlineData(false, 14)]
    [InlineData(true, 7.4)]
    public async Task DrawnPointWithoutPreferenceOrRoomKeepsTheDirectDetourSearch(bool green, double target)
    {
        var generator = new RouteGenerator(provider, store, new(), clock, new FixedWoodland(null));
        var drawn = Point(90, 3);
        var preview = await generator.PreviewAsync(new(Fixtures.Start, [drawn], PreferGreen: green), default);
        await generator.GenerateAsync(Fixtures.Loop(target) with { DrawnRouteId = preview.Id, PreferGreen = green }, default);
        Assert.Empty(LoopsBeyond(drawn));
        Assert.InRange(provider.ViaQueries.Count - 1, 2, 9);
    }

    [Fact]
    public async Task RejectsMissingExpiredOrChangedDrafts()
    {
        var generator = Generator();
        await Assert.ThrowsAsync<RouteValidationException>(() => generator.GenerateAsync(Fixtures.Loop() with { DrawnRouteId = Guid.NewGuid() }, default));
        var preview = await generator.PreviewAsync(new(Fixtures.Start, [Point(90)]), default);
        await Assert.ThrowsAsync<RouteValidationException>(() => generator.GenerateAsync(
            Fixtures.Loop(10) with { DrawnRouteId = preview.Id, PreferGreen = true }, default));
        await Assert.ThrowsAsync<RouteValidationException>(() => generator.GenerateAsync(
            Fixtures.Loop(10) with { DrawnRouteId = preview.Id, Start = Point(180, .1) }, default));
    }

    [Fact]
    public async Task RejectsClickedPointsOutsideTheRequestedRadius()
    {
        var generator = Generator();
        var preview = await generator.PreviewAsync(new(Fixtures.Start, [Point(90, 2)]), default);
        var error = await Assert.ThrowsAsync<RouteValidationException>(() => generator.GenerateAsync(
            Fixtures.Loop(10, 1) with { DrawnRouteId = preview.Id }, default));
        Assert.Contains("outside the radius", error.Message);
    }

    [Fact]
    public async Task PreviewValidatesPointCountCoordinatesAndDuplicates()
    {
        foreach (var request in new RoutePreviewRequest[] {
            new(Fixtures.Start, []), new(Fixtures.Start, Enumerable.Repeat(Point(90), 21).ToArray()),
            new(Fixtures.Start, [new(100, 0)]), new(Fixtures.Start, [Fixtures.Start]), new(null, [Point(90)])
        })
            await Assert.ThrowsAsync<RouteValidationException>(() => Generator().PreviewAsync(request, default));
        Assert.Empty(provider.ViaQueries);
    }

    [Fact]
    public async Task DraftStoreUsesTheSameExpiryAsItsPreview()
    {
        using var cache = new CandidateStore(clock);
        var generator = new RouteGenerator(provider, cache, new(), clock);
        var preview = await generator.PreviewAsync(new(Fixtures.Start, [Point(90)]), default);
        Assert.NotNull(cache.GetDraft(preview.Id));
        clock.Now = clock.Now.AddMinutes(31);
        Assert.Null(cache.GetDraft(preview.Id));
        Assert.Null(cache.Get(preview.Id));
    }

    [Fact]
    public void JoiningPreservesLengthElevationAndPerEdgeQuality()
    {
        var junction = Point(90);
        var prefix = new RouteResult([Fixtures.Start, junction], 1, 10, SegmentQualities: [new(.8, true)]);
        var suffix = new RouteResult([junction, Point(0), Fixtures.Start], 2, 20, SegmentQualities: [new(.4, false), new(.6, null)]);
        var joined = RouteJoin.Append(prefix, suffix);
        Assert.Equal(4, joined.Geometry.Count);
        Assert.Equal(3, joined.DistanceKm);
        Assert.Equal(30, joined.ElevationGainM);
        Assert.Equal(3, joined.SegmentQualities!.Count);
        Assert.True(joined.SegmentQualities[0].Unpaved);
        Assert.Null(joined.SegmentQualities[2].Unpaved);
        Assert.Throws<ProviderException>(() => RouteJoin.Append(prefix, suffix with { Geometry = [Point(180), Fixtures.Start] }));
    }
}
