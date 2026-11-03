using System.Collections.Concurrent;

namespace Meander.Core;

public sealed class RouteGenerator(IRoutingProvider provider, ICandidateStore store,
    GenerationOptions options, TimeProvider clock, IWoodlandProvider? woodlands = null)
{
    public async Task<RouteCandidate> PreviewAsync(RoutePreviewRequest request, CancellationToken cancellationToken)
    {
        var errors = RequestValidation.Check(new(request.Start, null, null, .5, request.Activity), options);
        if (request.Points is null || request.Points.Count is < 1 or > 20 || request.Points.Any(p => !Geo.IsValid(p)))
            errors["points"] = ["Choose between 1 and 20 valid route points."];
        if (errors.Count > 0) throw new RouteValidationException("Check the drawn route points.", errors);
        Location[] points = [request.Start!, .. request.Points!];
        if (points.Zip(points.Skip(1), Geo.DistanceKm).Any(distance => distance < .001))
            throw new RouteValidationException("Choose different consecutive route points.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(options.DeadlineSeconds, 20)));
        var snaps = await RetryInvalidPreviewResponse(() => provider.SnapAsync(points, request.Activity, options.SnapRadiusM, deadline.Token), deadline.Token);
        if (snaps.Count != points.Length)
            throw new ProviderException(ProviderFailure.InvalidResponse, "The routing service returned invalid snapping data.");
        if (snaps.Any(s => s is null))
            throw new RouteValidationException("No walkable path was found near one of the clicked points. Move that point closer to a path.");
        var preferences = new RoutePreferences(request.PreferGreen, request.PreferUnpaved);
        // Every clicked point is intentional. Do not remove its out-and-back legs.
        var route = await RetryInvalidPreviewResponse(() => provider.ViaPointsAsync(new(snaps.Select(s => s!.Location).ToArray(), request.Activity,
            Preferences: preferences), deadline.Token), deadline.Token);
        ValidateGeometry(route);
        var warnings = new List<RouteWarning>(route.Warnings ?? []);
        if (points.Select((point, i) => Geo.DistanceKm(point, snaps[i]!.Location) * 1000).Any(d => d > options.SnapWarningM))
            warnings.Add(new("drawnPointSnapped", $"A clicked point was moved more than {options.SnapWarningM:F0} m to a walkable path. Check the drawn section on the map."));
        route = route with { Warnings = warnings };
        var start = MakeEndpoint(points[0], snaps[0]!);
        var end = MakeEndpoint(points[^1], snaps[^1]!);
        var quality = RouteQuality.Measure(route);
        var candidate = new RouteCandidate(Guid.NewGuid(), clock.GetUtcNow().AddMinutes(options.CacheTtlMinutes),
            LineString.From(route.Geometry), route.DistanceKm, route.ElevationGainM,
            Geo.Sample(route.Geometry).Max(p => Geo.DistanceKm(points[0], p)), 0, CandidateStatus.Qualified,
            start, end, warnings.ToArray(), quality.GreenScore, quality.UnpavedPercent, quality.SurfaceKnownPercent,
            snaps.Select(s => s!.Location).ToArray());
        store.Put(candidate);
        store.PutDraft(candidate.Id, new(route, start, end, request.Points!.ToArray(), request.Activity, preferences), candidate.ExpiresAt!.Value);
        return candidate;
    }

    private static async Task<T> RetryInvalidPreviewResponse<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try { return await call(); }
        catch (ProviderException ex) when (ex.Failure == ProviderFailure.InvalidResponse && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(200, cancellationToken);
            return await call();
        }
    }

    public async Task<IReadOnlyList<RouteCandidate>> GenerateAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        var errors = RequestValidation.Check(request, options);
        if (errors.Count > 0) throw new RouteValidationException("Check the route inputs.", errors);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.DeadlineSeconds));
        var ct = deadline.Token;
        var results = new ConcurrentBag<RouteResult>();
        var failures = new ConcurrentQueue<ProviderFailure>();
        var warnings = new List<RouteWarning>();
        var preferences = new RoutePreferences(request.PreferGreen, request.PreferUnpaved);
        RouteDraft? draft = null;
        if (request.DrawnRouteId is { } draftId)
        {
            draft = store.GetDraft(draftId) ?? throw new RouteValidationException("The drawn section has expired. Refresh its preview before generating routes.");
            if (draft.Activity != request.Activity || draft.Preferences != preferences || Geo.DistanceKm(draft.Start.Requested, request.Start!) > .000001)
                throw new RouteValidationException("The drawn section belongs to different settings. Refresh its preview before generating routes.");
            if (request.RadiusKm is { } radius && draft.RequestedPoints.Any(p => Geo.DistanceKm(request.Start!, p) > radius))
                throw new RouteValidationException("A clicked route point is outside the radius. Move the point or increase the radius.");
        }
        var fixedDistanceKm = draft?.Route.DistanceKm ?? 0;
        var distanceRange = RouteDistanceRange.For(request, options);
        var flexibleDistance = request.DistanceFlexibilityPercent > 0;
        var favourScenery = flexibleDistance && (request.PreferGreen || request.PreferUnpaved);
        double SearchDistance(int index) => Math.Max(.01, (!flexibleDistance ? request.TargetDistanceKm : (index % 5) switch
        {
            1 => distanceRange.MinKm, 2 => distanceRange.MaxKm,
            3 => (distanceRange.MinKm + request.TargetDistanceKm) / 2,
            4 => (distanceRange.MaxKm + request.TargetDistanceKm) / 2,
            _ => request.TargetDistanceKm
        }) - fixedDistanceKm);
        EndpointInfo start;
        EndpointInfo end;
        WoodlandMap? woodland = null;

        async Task<RouteResult?> Attempt(Func<Task<RouteResult>> call)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var result = await call();
                ValidateGeometry(result);
                if (request.AvoidBacktracking) result = RouteCleanup.RemoveBacktracking(result);
                if (draft is not null) result = RouteJoin.Append(draft.Route, result);
                results.Add(result);
                return result;
            }
            catch (ProviderException ex) { failures.Enqueue(ex.Failure); return null; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { failures.Enqueue(ProviderFailure.Timeout); return null; }
        }

        try
        {
            var isLoop = request.End is null || Geo.DistanceKm(request.Start!, request.End) < .000001;
            var requestedEnd = isLoop ? request.Start! : request.End!;
            var snapped = await provider.SnapAsync(isLoop ? [request.Start!] : [request.Start!, requestedEnd],
                request.Activity, options.SnapRadiusM, ct);
            if (snapped.Count != (isLoop ? 1 : 2))
                throw new ProviderException(ProviderFailure.InvalidResponse, "The routing service returned invalid snapping data.");
            if (snapped[0] is not { } snappedStart || (isLoop ? snapped[0] : snapped[1]) is not { } snappedEnd)
                throw new RouteValidationException("No walkable path was found near the selected location. Choose a point closer to a path.");
            start = draft?.Start ?? MakeEndpoint(request.Start!, snappedStart);
            end = isLoop && draft is not null ? start : MakeEndpoint(requestedEnd, snappedEnd);
            var searchStart = draft?.Route.Geometry[^1] ?? start.Snapped;
            var completionIsLoop = Geo.DistanceKm(searchStart, end.Snapped) < .000001;
            if (request.PreferGreen && woodlands is not null)
            {
                woodland = await woodlands.FindAsync(Geo.Interpolate(searchStart, end.Snapped, .5),
                    distanceRange.MaxKm / 2, ct);
                if (woodland is null)
                    warnings.Add(new("woodlandUnavailable", "Could not look up nearby woodland. These routes may not follow your woodland preference. Try again later."));
            }
            var woodlandTargets = woodland?.Targets(searchStart, end.Snapped,
                Math.Max(.01, distanceRange.MaxKm - fixedDistanceKm), request.Start!, request.RadiusKm) ?? [];

            async Task SearchWoodland(int budget)
            {
                var firstCount = budget / 2;
                async Task<(WoodlandTarget Target, int Variant, double Scale)> Probe(int i)
                {
                    var target = woodlandTargets[i % woodlandTargets.Count];
                    var variant = i / woodlandTargets.Count;
                    var desired = SearchDistance(i);
                    var points = target.Waypoints(searchStart, end.Snapped, desired, variant);
                    var result = await Attempt(() => provider.ViaPointsAsync(new(points, request.Activity, Preferences: preferences), ct));
                    var access = target.Points.Min(p => Geo.DistanceKm(searchStart, p) + Geo.DistanceKm(p, end.Snapped)) * 1.25;
                    // Adjust the forest circuit, not the approach from the user's start.
                    var actual = result?.DistanceKm - fixedDistanceKm;
                    var scale = actual is > 0 ? Math.Clamp((desired - access) / Math.Max(.2, actual.Value - access), .3, 2.5) : .6;
                    return (target, variant, scale);
                }
                var probes = await Task.WhenAll(Enumerable.Range(0, firstCount).Select(Probe));
                await Task.WhenAll(Enumerable.Range(0, budget - firstCount).Select(i =>
                {
                    var probe = probes[i % probes.Length];
                    var points = probe.Target.Waypoints(searchStart, end.Snapped, SearchDistance(i + firstCount),
                        probe.Variant + 1, probe.Scale);
                    return Attempt(() => provider.ViaPointsAsync(new(points, request.Activity, Preferences: preferences), ct));
                }));
            }
            if (start.SnapDistanceM > options.SnapWarningM)
                warnings.Add(new("startSnapped", $"Start moved {start.SnapDistanceM:F0} m to the nearest routable path."));
            if (!isLoop && end.SnapDistanceM > options.SnapWarningM)
                warnings.Add(new("endSnapped", $"End moved {end.SnapDistanceM:F0} m to the nearest routable path."));

            if (draft is not null && completionIsLoop && fixedDistanceKm > (flexibleDistance ? distanceRange.MaxKm : request.TargetDistanceKm) + .000001)
                throw new RouteValidationException("The drawn section is already longer than the requested route. Increase the distance or remove a point.", minimumDistanceKm: fixedDistanceKm);
            if (draft is not null && completionIsLoop) results.Add(draft.Route);

            if (draft is not null && completionIsLoop && distanceRange.MaxKm - fixedDistanceKm < .5)
            {
                // The user already drew a complete loop. Keep it intact.
            }
            else if (completionIsLoop && woodlandTargets.Count > 0)
            {
                // Eight directed forest circuits, plus two general alternatives as fallback.
                // This keeps the existing ten-directions-call budget.
                await Task.WhenAll(SearchWoodland(8), Task.WhenAll(Enumerable.Range(0, 2).Select(i =>
                    Attempt(() => provider.RoundTripAsync(new(searchStart, SearchDistance(i), 3 + i,
                        Random.Shared.Next(1, int.MaxValue), request.Activity, preferences), ct)))));
            }
            else if (completionIsLoop)
            {
                var calls = new List<Task<RouteResult?>>();
                var probes = new ConcurrentBag<(double RequestedKm, double ActualKm)>();
                var rounds = request.RadiusKm.HasValue ? 6 : 10;
                void QueueRoundTrip(int i, double lengthKm)
                {
                    var query = new RoundTripQuery(searchStart, lengthKm, 3 + i % 3,
                        Random.Shared.Next(1, int.MaxValue), request.Activity, preferences);
                    async Task<RouteResult?> Probe()
                    {
                        var result = await Attempt(() => provider.RoundTripAsync(query, ct));
                        if (result is not null) probes.Add((lengthKm, result.DistanceKm - fixedDistanceKm));
                        return result;
                    }
                    calls.Add(Probe());
                }
                var firstRoundCount = rounds / 2;
                for (var i = 0; i < firstRoundCount; i++) QueueRoundTrip(i, SearchDistance(i));
                await Task.WhenAll(calls);
                calls.Clear();
                // Green weighting and spur removal can change actual distance.
                // Use the second half of the existing budget to compensate.
                var closest = probes.OrderBy(p => Math.Abs(p.ActualKm + fixedDistanceKm - request.TargetDistanceKm)).FirstOrDefault();
                var followupScale = closest.ActualKm > 0 && !distanceRange.Contains(closest.ActualKm + fixedDistanceKm)
                    ? closest.RequestedKm / closest.ActualKm : 1;
                for (var i = firstRoundCount; i < rounds; i++) QueueRoundTrip(i,
                    Math.Clamp(SearchDistance(i) * followupScale, SearchDistance(0) * .5, Math.Min(100, SearchDistance(0) * 1.5)));
                if (request.RadiusKm is { } radius)
                    for (var i = 0; i < 4; i++)
                    {
                        var extent = Math.Min(radius * .9, SearchDistance(i) / 6) * (i % 2 == 0 ? .75 : 1);
                        var bearing = i * 90.0;
                        var points = new List<Location> { searchStart };
                        for (var j = 0; j < 3; j++)
                            points.Add(Geo.Destination(request.Start!, bearing + j * 120, extent));
                        points.Add(searchStart);
                        var query = new ViaPointsQuery(points, request.Activity, Preferences: preferences);
                        calls.Add(Attempt(() => provider.ViaPointsAsync(query, ct)));
                    }
                await Task.WhenAll(calls);
            }
            else
            {
                var baseline = await Attempt(() => provider.ViaPointsAsync(new([searchStart, end.Snapped], request.Activity, true), ct));
                if (baseline is not null)
                {
                    if ((flexibleDistance ? distanceRange.MaxKm : request.TargetDistanceKm) + .000001 < baseline.DistanceKm)
                        throw new RouteValidationException(draft is null ? "The target is shorter than the shortest route between these locations."
                            : "The drawn section plus the shortest connection is longer than the requested route. Increase the distance or remove a point.",
                            minimumDistanceKm: baseline.DistanceKm);

                    async Task SearchSide(int side)
                    {
                        var bearing = Geo.Bearing(searchStart, end.Snapped);
                        var searchTarget = flexibleDistance
                            ? side > 0 ? distanceRange.MaxKm : Math.Max(distanceRange.MinKm, baseline.DistanceKm)
                            : request.TargetDistanceKm;
                        double low = 0, high = (searchTarget - fixedDistanceKm) / 2;
                        var offset = (searchTarget - fixedDistanceKm) / 8;
                        for (var i = 0; i < (woodlandTargets.Count > 0 ? 1 : 4) && !ct.IsCancellationRequested; i++)
                        {
                            var first = Geo.Destination(Geo.Interpolate(searchStart, end.Snapped, 1.0 / 3), bearing + side * 90, offset);
                            var second = Geo.Destination(Geo.Interpolate(searchStart, end.Snapped, 2.0 / 3), bearing - side * 90, offset);
                            if (request.RadiusKm is { } radius)
                            {
                                first = Geo.Inside(first, request.Start!, radius * .9);
                                second = Geo.Inside(second, request.Start!, radius * .9);
                            }
                            var result = await Attempt(() => provider.ViaPointsAsync(new([searchStart, first, second, end.Snapped], request.Activity, Preferences: preferences), ct));
                            if (result is null) { high = offset; offset = (low + high) / 2; continue; }
                            if (Math.Abs(result.DistanceKm - searchTarget) / searchTarget <= options.DistanceTolerance) break;
                            if (result.DistanceKm < searchTarget)
                            { low = offset; offset = Math.Min(high, offset * 2); if (offset <= low) break; }
                            else { high = offset; offset = (low + high) / 2; }
                        }
                    }
                    if (woodlandTargets.Count > 0) await Task.WhenAll(SearchWoodland(6), SearchSide(1), SearchSide(-1));
                    else await Task.WhenAll(SearchSide(1), SearchSide(-1));
                }
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(ProviderFailure.Timeout, "Route generation timed out. Try a shorter distance or a different start.", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (results.IsEmpty) ThrowNoResults(failures, request.AvoidBacktracking);
        if (!failures.IsEmpty || ct.IsCancellationRequested)
            warnings.Add(new("partialSearch", "Some routing attempts failed or timed out. These are the best routes found so far."));

        var evaluated = results.Select(result =>
        {
            var shape = new RouteShape(result.Geometry);
            var maxDistance = Geo.Sample(result.Geometry).Max(p => Geo.DistanceKm(request.Start!, p));
            var error = Math.Abs(result.DistanceKm - request.TargetDistanceKm) / request.TargetDistanceKm;
            var quality = RouteQuality.Measure(result);
            var forest = woodland?.Measure(result.Geometry);
            var measuredQuality = forest is null ? quality : quality with { GreenScore = forest.Percent / 100 };
            var sceneryPenalty = request.PreferGreen && forest is not null
                ? (request.PreferUnpaved ? .7 * (1 - forest.Percent / 100) + .3 * (1 - (quality.UnpavedPercent ?? 0) / 100)
                    : 1 - forest.Percent / 100)
                : measuredQuality.Penalty(preferences);
            var accessPenalty = Math.Min(1, (forest?.FirstEntryKm ?? result.DistanceKm) / Math.Max(.5, result.DistanceKm / 2));
            var status = request.RadiusKm is { } radius && maxDistance > radius + .000001 ? CandidateStatus.Diagnostic
                : distanceRange.Contains(result.DistanceKm) ? CandidateStatus.Qualified : CandidateStatus.BestEffort;
            var score = favourScenery && status == CandidateStatus.Qualified
                ? forest is not null
                    ? .05 * error + .1 * shape.RetracingFraction + .1 * accessPenalty + .75 * sceneryPenalty
                    : .15 * error + .15 * shape.RetracingFraction + .7 * sceneryPenalty
                : .8 * error + .2 * shape.RetracingFraction + .25 * sceneryPenalty;
            return new Evaluation(result, shape, maxDistance,
                score, status, quality, forest);
        }).OrderBy(e => e.Status)
            .ThenBy(e => e.Status == CandidateStatus.Diagnostic ? e.MaxDistance : 0)
            .ThenBy(e => e.Score).ToList();

        var selected = new List<Evaluation>();
        foreach (var evaluation in evaluated)
        {
            if (selected.Any(s => s.Shape.Overlap(evaluation.Shape) > .7)) continue;
            selected.Add(evaluation);
            if (selected.Count == request.CandidateCount) break;
        }

        if (selected.Count < request.CandidateCount)
            warnings.Add(new("fewerRoutes", $"Found {selected.Count} distinct route(s); requested {request.CandidateCount}. Try moving the start or changing the distance."));
        var expires = clock.GetUtcNow().AddMinutes(options.CacheTtlMinutes);
        var candidates = selected.Select(e =>
        {
            var allWarnings = new List<RouteWarning>(warnings);
            if (e.Result.Warnings is { } providerWarnings) allWarnings.AddRange(providerWarnings);
            if (request.PreferGreen && e.Quality.GreenScore is null && e.Woodland is null && woodlands is null)
                allWarnings.Add(new("greenDataUnavailable", "Not enough greenery data is available to compare this route's green preference."));
            if (request.PreferGreen && e.Woodland is { Percent: < 1 })
                allWarnings.Add(new("woodlandNotReached", "No substantial woodland was reached within this distance. Try a longer route or a start nearer a forest."));
            if (request.PreferUnpaved && (e.Quality.SurfaceKnownPercent ?? 0) < 90)
                allWarnings.Add(new("surfaceDataIncomplete", $"Surface is known for {e.Quality.SurfaceKnownPercent ?? 0:F0}% of this route. The unpaved share counts confirmed sections only."));
            if (!distanceRange.Contains(e.Result.DistanceKm))
                allWarnings.Add(new("distanceMiss", $"Distance found: {e.Result.DistanceKm:F2} km; accepted range: {distanceRange.MinKm:F2}–{distanceRange.MaxKm:F2} km."));
            if (e.Status == CandidateStatus.Diagnostic)
                allWarnings.Add(new("radiusExceeded", $"Diagnostic route: reaches {e.MaxDistance:F2} km from start, beyond the {request.RadiusKm:F2} km radius."));
            var candidate = new RouteCandidate(Guid.NewGuid(), expires, LineString.From(e.Result.Geometry), e.Result.DistanceKm,
                e.Result.ElevationGainM, e.MaxDistance, e.Score, e.Status, start, end, allWarnings.DistinctBy(w => w.Code).ToArray(),
                e.Quality.GreenScore, e.Quality.UnpavedPercent, e.Quality.SurfaceKnownPercent, WoodlandPercent: e.Woodland?.Percent);
            store.Put(candidate);
            return candidate;
        }).ToArray();
        return candidates;
    }

    private static EndpointInfo MakeEndpoint(Location requested, SnapResult result)
    {
        if (!Geo.IsValid(result.Location)) throw new ProviderException(ProviderFailure.InvalidResponse, "The routing service returned an invalid snapped location.");
        return new(requested, result.Location, Geo.DistanceKm(requested, result.Location) * 1000);
    }

    private static void ValidateGeometry(RouteResult result)
    {
        if (!double.IsFinite(result.DistanceKm) || result.DistanceKm <= 0 || result.DistanceKm > 1000 ||
            result.Geometry.Count is < 2 or > 100000 || result.Geometry.Any(p => !Geo.IsValid(p)) ||
            (result.ElevationGainM is { } elevation && (!double.IsFinite(elevation) || elevation < 0)))
            throw new ProviderException(ProviderFailure.InvalidResponse, "The routing service returned invalid route data.");
        // Bound sampling and scoring work even for malformed upstream responses.
        var length = result.Geometry.Zip(result.Geometry.Skip(1), Geo.DistanceKm).Sum();
        if (length is <= 0 or > 1000)
            throw new ProviderException(ProviderFailure.InvalidResponse, "The routing service returned an invalid route geometry.");
    }

    private static void ThrowNoResults(ConcurrentQueue<ProviderFailure> failures, bool avoidBacktracking)
    {
        var failure = failures.FirstOrDefault(f => f != ProviderFailure.NoRoute, ProviderFailure.NoRoute);
        if (failure == ProviderFailure.NoRoute)
            throw new RouteValidationException("No usable route was found. The area may have water, disconnected paths, or a sparse walking network. Try another start or distance." +
                (avoidBacktracking ? " You can also turn off out-and-back avoidance if no circular paths are available." : ""));
        throw new ProviderException(failure, failure switch
        {
            ProviderFailure.Timeout => "The routing service timed out. Try again shortly.",
            ProviderFailure.Throttled => "The routing service is at its request limit. Try again shortly.",
            ProviderFailure.InvalidResponse => "The routing service returned an invalid response.",
            _ => "The routing service is temporarily unavailable. Try again shortly."
        });
    }
    private sealed record Evaluation(RouteResult Result, RouteShape Shape, double MaxDistance, double Score, CandidateStatus Status, RouteQuality Quality,
        WoodlandMeasure? Woodland);
}
