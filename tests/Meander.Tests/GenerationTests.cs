using Meander.Core;

namespace Meander.Tests;

public sealed class GenerationTests
{
    [Theory]
    [InlineData(8.8)]
    [InlineData(11.2)]
    public async Task FlexibleDistanceLetsSceneryBeatExactDistanceInsideTheAllowedRange(double scenicDistance)
    {
        var provider = new FakeRouting();
        var count = 0;
        provider.Round = (_, _) =>
        {
            var variant = Interlocked.Increment(ref count) % 3;
            var route = Fixtures.Petal(variant * 120, distance: variant == 0 ? 12 : variant == 1 ? 10 : scenicDistance);
            return Task.FromResult(route with { SegmentQualities = Enumerable.Repeat(
                new SegmentQuality(variant == 0 ? 1 : variant == 1 ? .2 : .9, variant != 1), 3).ToArray() });
        };
        var generator = new RouteGenerator(provider, new MemoryStore(), new(), new TestClock());
        var request = Fixtures.Loop(10) with { PreferGreen = true, PreferUnpaved = true };
        var normal = await generator.GenerateAsync(request, default);
        Assert.Equal(10, normal[0].DistanceKm);
        var flexible = await generator.GenerateAsync(request with { DistanceFlexibilityPercent = 15 }, default);
        Assert.Equal(scenicDistance, flexible[0].DistanceKm);
        Assert.Equal(CandidateStatus.Qualified, flexible[0].Status);
        Assert.Equal(CandidateStatus.BestEffort, flexible.Single(r => r.DistanceKm == 12).Status);
    }

    [Fact]
    public async Task FlexibleSearchExploresBothSidesOfTheTargetWithinTheExistingCallBudget()
    {
        var lengths = new System.Collections.Concurrent.ConcurrentBag<double>();
        var provider = new FakeRouting { Round = (query, _) =>
        {
            lengths.Add(query.DistanceKm);
            return Task.FromResult(Fixtures.Petal(query.Seed % 360, distance: query.DistanceKm));
        } };
        await new RouteGenerator(provider, new MemoryStore(), new(), new TestClock())
            .GenerateAsync(Fixtures.Loop(10) with { DistanceFlexibilityPercent = 15 }, default);
        Assert.Equal(10, provider.Rounds);
        Assert.Contains(8.5, lengths);
        Assert.Contains(11.5, lengths);
        Assert.All(lengths, length => Assert.InRange(length, 8.5, 11.5));
    }

    [Theory]
    [InlineData(-1)] [InlineData(1)] [InlineData(4.99)] [InlineData(30.01)] [InlineData(double.NaN)]
    public void InvalidFlexibilityIsRejected(double margin)
    {
        var errors = RequestValidation.Check(Fixtures.Loop() with { DistanceFlexibilityPercent = margin }, new());
        Assert.Contains("distanceFlexibilityPercent", errors.Keys);
    }

    [Fact]
    public void FlexibleRangeRespectsMinimumAndConfiguredMaximum()
    {
        Assert.Equal(.5, RouteDistanceRange.For(Fixtures.Loop(.5) with { DistanceFlexibilityPercent = 30 }, new()).MinKm);
        Assert.Equal(60, RouteDistanceRange.For(Fixtures.Loop(60) with { DistanceFlexibilityPercent = 30 }, new()).MaxKm);
    }

    [Fact]
    public async Task SecondRoundTripBatchCompensatesDistanceWithoutIncreasingCallBudget()
    {
        var provider = new FakeRouting();
        provider.Round = (query, _) => Task.FromResult(Fixtures.Petal(query.Seed % 360, distance: query.DistanceKm * 1.2));
        var routes = await new RouteGenerator(provider, new MemoryStore(), new(), new TestClock())
            .GenerateAsync(Fixtures.Loop(), default);
        Assert.Equal(10, provider.Rounds);
        Assert.Equal(CandidateStatus.Qualified, routes[0].Status);
        Assert.InRange(routes[0].DistanceKm, 4.999, 5.001);
    }
    [Fact]
    public async Task GreenPreferenceRanksGreenerRoutesWithinDistanceToleranceFirst()
    {
        var provider = new FakeRouting();
        var counter = 0;
        provider.Round = (query, _) =>
        {
            Assert.True(query.Preferences!.PreferGreen);
            var variant = Interlocked.Increment(ref counter) % 3;
            var result = Fixtures.Petal(variant * 120, distance: variant == 0 ? 6 : variant == 1 ? 5 : 5.15);
            return Task.FromResult(result with { SegmentQualities = Enumerable.Repeat(new SegmentQuality(variant == 0 ? 1 : variant == 1 ? .1 : .9), 3).ToArray() });
        };
        var routes = await new RouteGenerator(provider, new MemoryStore(), new(), new TestClock())
            .GenerateAsync(Fixtures.Loop() with { PreferGreen = true }, default);
        Assert.Equal(CandidateStatus.Qualified, routes[0].Status);
        Assert.InRange(routes[0].GreenScore!.Value, .89, .91);
        Assert.Equal(CandidateStatus.BestEffort, routes[^1].Status);
    }

    [Fact]
    public async Task UnknownSurfaceWarnsInsteadOfPretendingPreferenceWasMet()
    {
        var provider = new FakeRouting { Round = (_, _) => Task.FromResult(Fixtures.Petal()) };
        var routes = await new RouteGenerator(provider, new MemoryStore(), new(), new TestClock())
            .GenerateAsync(Fixtures.Loop() with { PreferGreen = true, PreferUnpaved = true }, default);
        var route = Assert.Single(routes);
        Assert.Null(route.UnpavedPercent);
        Assert.Contains(route.Warnings, w => w.Code == "surfaceDataIncomplete");
        Assert.Contains(route.Warnings, w => w.Code == "greenDataUnavailable");
    }

    [Fact]
    public async Task BacktrackingAvoidanceCanBeDisabledForSparseNetworks()
    {
        var provider = new FakeRouting { Round = (_, _) => Task.FromResult(new RouteResult(
            [Fixtures.Start, Geo.Destination(Fixtures.Start, 90, 1), Fixtures.Start], 2, 0)) };
        var generator = new RouteGenerator(provider, new MemoryStore(), new(), new TestClock());
        await Assert.ThrowsAsync<RouteValidationException>(() => generator.GenerateAsync(Fixtures.Loop(2), default));
        Assert.NotEmpty(await generator.GenerateAsync(Fixtures.Loop(2) with { AvoidBacktracking = false }, default));
    }

    private readonly FakeRouting provider = new();
    private readonly MemoryStore store = new();
    private readonly TestClock clock = new();
    private RouteGenerator Generator(GenerationOptions? options = null) => new(provider, store, options ?? new(), clock);

    [Theory]
    [InlineData(.49)] [InlineData(60.01)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public async Task RejectsInvalidDistanceBeforeCallingProvider(double distance)
    {
        var error = await Assert.ThrowsAsync<RouteValidationException>(() => Generator().GenerateAsync(Fixtures.Loop(distance), default));
        Assert.Contains("targetDistanceKm", error.Errors.Keys);
        Assert.Equal(0, provider.Rounds);
    }

    [Theory] [InlineData(.5)] [InlineData(60)]
    public async Task AcceptsDistanceBoundaries(double distance)
    {
        var routes = await Generator().GenerateAsync(Fixtures.Loop(distance), default);
        Assert.NotEmpty(routes);
        Assert.All(routes, r => Assert.Equal(CandidateStatus.Qualified, r.Status));
    }

    [Fact]
    public async Task RejectsEndOutsideRadius()
    {
        var request = Fixtures.Loop(radius: 1) with { End = Geo.Destination(Fixtures.Start, 90, 2) };
        var error = await Assert.ThrowsAsync<RouteValidationException>(() => Generator().GenerateAsync(request, default));
        Assert.Contains("end", error.Errors.Keys);
    }

    [Fact]
    public void ValidatesMissingStartActivityRadiusAndCandidateCount()
    {
        var errors = RequestValidation.Check(new(null, null, 0, 5, (Activity)100, 0), new());
        Assert.Equal(4, errors.Count);
    }

    [Fact]
    public async Task ExplicitSameEndUsesRoundTrips()
    {
        await Generator().GenerateAsync(Fixtures.Loop() with { End = Fixtures.Start }, default);
        Assert.Equal(10, provider.Rounds);
        Assert.Empty(provider.ViaQueries);
    }

    [Fact]
    public async Task RadiusSearchUsesConstrainedWaypointsAndDoesNotInferImpossibleDistance()
    {
        var routes = await Generator().GenerateAsync(Fixtures.Loop(60, .1), default);
        Assert.Equal(6, provider.Rounds);
        Assert.Equal(4, provider.ViaQueries.Count);
        Assert.All(provider.ViaQueries.SelectMany(q => q.Points), p => Assert.True(Geo.DistanceKm(Fixtures.Start, p) <= .1));
        Assert.NotEmpty(routes);
    }

    [Fact]
    public async Task ShorterThanMinimumReturnsMeasuredMinimum()
    {
        provider.Via = (q, _) => Task.FromResult(new RouteResult(q.Points, 7.25, null));
        var error = await Assert.ThrowsAsync<RouteValidationException>(() => Generator().GenerateAsync(
            Fixtures.Loop() with { End = Geo.Destination(Fixtures.Start, 90, 2) }, default));
        Assert.Equal(7.25, error.MinimumDistanceKm);
        Assert.True(Assert.Single(provider.ViaQueries).Shortest);
    }

    [Fact]
    public async Task DetourRefinesOffsetOnBothSidesAndKeepsBestObservations()
    {
        provider.Via = (q, _) => Task.FromResult(new RouteResult(q.Points, q.Shortest ? 2 :
            2 + Geo.DistanceKm(Geo.Interpolate(q.Points[0], q.Points[^1], 1.0 / 3), q.Points[1]) * 2, null));
        var routes = await Generator().GenerateAsync(Fixtures.Loop() with { End = Geo.Destination(Fixtures.Start, 90, 2) }, default);
        Assert.InRange(provider.ViaQueries.Count, 5, 9);
        Assert.Contains(provider.ViaQueries, q => q.Points.Count == 4 && q.Points[1].Latitude > Fixtures.Start.Latitude);
        Assert.Contains(provider.ViaQueries, q => q.Points.Count == 4 && q.Points[1].Latitude < Fixtures.Start.Latitude);
        Assert.NotEmpty(routes);
    }

    [Fact]
    public async Task RadiusViolationsAreReturnedAsDiagnosticsWithOriginalCenter()
    {
        provider.Round = (_, _) => Task.FromResult(Fixtures.Petal(extent: 2));
        provider.Via = (_, _) => Task.FromResult(Fixtures.Petal(180, extent: 2));
        var routes = await Generator().GenerateAsync(Fixtures.Loop(radius: 1), default);
        Assert.All(routes, r =>
        {
            Assert.Equal(CandidateStatus.Diagnostic, r.Status);
            Assert.True(r.MaxDistanceFromStartKm > 1.9);
            Assert.Contains(r.Warnings, w => w.Code == "radiusExceeded");
        });
    }

    [Fact]
    public async Task QualifyingRoutesRankAboveDistanceMisses()
    {
        var counter = 0;
        provider.Round = (_, _) => { var i = Interlocked.Increment(ref counter); return Task.FromResult(Fixtures.Petal(i * 90, distance: i % 2 == 0 ? 5 : 7)); };
        var routes = await Generator().GenerateAsync(Fixtures.Loop(), default);
        Assert.Equal(CandidateStatus.Qualified, routes[0].Status);
    }

    [Fact]
    public async Task DuplicatesProduceFewerResultsWarningAndCacheExpiry()
    {
        provider.Round = (_, _) => Task.FromResult(Fixtures.Petal());
        var route = Assert.Single(await Generator().GenerateAsync(Fixtures.Loop(), default));
        Assert.Contains(route.Warnings, w => w.Code == "fewerRoutes");
        Assert.Equal(clock.Now.AddMinutes(30), route.ExpiresAt);
        Assert.Equal(route, store.Get(route.Id));
    }

    [Fact]
    public async Task MissingElevationStaysNullAndDistanceMissWarns()
    {
        provider.Round = (_, _) => Task.FromResult(Fixtures.Petal(distance: 6) with { ElevationGainM = null });
        var route = Assert.Single(await Generator().GenerateAsync(Fixtures.Loop(), default));
        Assert.Null(route.ElevationGainM);
        Assert.Equal(CandidateStatus.BestEffort, route.Status);
        Assert.Contains(route.Warnings, w => w.Code == "distanceMiss");
    }

    [Fact]
    public async Task SnappingWarnsAboveTwoHundredMetersAndKeepsRequestedStart()
    {
        provider.Snap = points => points.Select(p => (SnapResult?)new(Geo.Destination(p, 90, .25), 250)).ToArray();
        var routes = await Generator().GenerateAsync(Fixtures.Loop(), default);
        Assert.All(routes, route =>
        {
            Assert.Equal(Fixtures.Start, route.Start.Requested);
            Assert.InRange(route.Start.SnapDistanceM, 249, 251);
            Assert.Contains(route.Warnings, w => w.Code == "startSnapped");
        });
    }

    [Fact]
    public async Task MissingNetworkSnapFailsClearly()
    {
        provider.Snap = _ => [null];
        var error = await Assert.ThrowsAsync<RouteValidationException>(() => Generator().GenerateAsync(Fixtures.Loop(), default));
        Assert.Contains("No walkable path", error.Message);
    }

    [Fact]
    public async Task PartialProviderFailureRetainsUsableRoutes()
    {
        var counter = 0;
        provider.Round = (_, _) => Interlocked.Increment(ref counter) == 1 ? Task.FromResult(Fixtures.Petal())
            : Task.FromException<RouteResult>(new ProviderException(ProviderFailure.Unavailable, "offline"));
        var route = Assert.Single(await Generator().GenerateAsync(Fixtures.Loop(), default));
        Assert.Contains(route.Warnings, w => w.Code == "partialSearch");
    }

    [Fact]
    public async Task DisconnectedNetworkNeverReturnsEmptySuccess()
    {
        provider.Round = (_, _) => Task.FromException<RouteResult>(new ProviderException(ProviderFailure.NoRoute, "island"));
        await Assert.ThrowsAsync<RouteValidationException>(() => Generator().GenerateAsync(Fixtures.Loop(), default));
    }

    [Fact]
    public async Task MalformedGeometryReturnsProviderFailure()
    {
        provider.Round = (_, _) => Task.FromResult(new RouteResult([new(double.NaN, 0), Fixtures.Start], 5, 0));
        var error = await Assert.ThrowsAsync<ProviderException>(() => Generator().GenerateAsync(Fixtures.Loop(), default));
        Assert.Equal(ProviderFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public async Task ClientCancellationPropagates()
    {
        provider.Round = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Fixtures.Petal(); };
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Generator().GenerateAsync(Fixtures.Loop(), cts.Token));
    }

    [Fact]
    public async Task GenerationDeadlineReturnsCompletedPartialResults()
    {
        var count = 0;
        provider.Round = async (_, ct) =>
        {
            if (Interlocked.Increment(ref count) == 1) return Fixtures.Petal();
            await Task.Delay(Timeout.Infinite, ct); return Fixtures.Petal();
        };
        var route = Assert.Single(await Generator(new() { DeadlineSeconds = 1 }).GenerateAsync(Fixtures.Loop(), default));
        Assert.Contains(route.Warnings, w => w.Code == "partialSearch");
    }
}
