namespace Meander.Core;

public sealed class RouteEditor(ICandidateStore store, GenerationOptions options, TimeProvider clock)
{
    public RouteEditSession Begin(RouteCandidate candidate, RouteRequest source)
    {
        var geometry = candidate.Geometry.Coordinates.Select(p => new Location(p[1], p[0], p.Length > 2 ? p[2] : null)).ToArray();
        var lengths = new double[geometry.Length];
        for (var i = 1; i < geometry.Length; i++) lengths[i] = lengths[i - 1] + Geo.DistanceKm(geometry[i - 1], geometry[i]);
        var sections = Math.Clamp((int)Math.Ceiling(lengths[^1] / .6), 3, 20);
        var selected = new SortedSet<int>();
        for (var i = 1; i < sections; i++)
        {
            var target = lengths[^1] * i / sections;
            var index = Array.BinarySearch(lengths, target);
            if (index < 0) index = ~index;
            if (index > 0 && index < geometry.Length - 1) selected.Add(index);
        }
        var points = selected.Select(i => geometry[i] with { ElevationM = null }).ToArray();
        var isLoop = source.End is null || Geo.DistanceKm(source.Start!, source.End) < .000001;
        var finish = isLoop ? source.Start! : source.End!;
        Location[] requested = [.. points, finish];
        var expires = clock.GetUtcNow().AddMinutes(options.CacheTtlMinutes);
        // Opening the editor preserves the exact found geometry and spends no routing calls.
        var preview = candidate with { Id = Guid.NewGuid(), ExpiresAt = expires, SearchId = null,
            SnappedPoints = [candidate.Start.Snapped, .. points, candidate.End.Snapped] };
        store.Put(preview);
        store.PutDraft(preview.Id, new(new(geometry, candidate.DistanceKm, candidate.ElevationGainM, candidate.Warnings),
            candidate.Start, candidate.End, requested, source.Activity, new(source.PreferGreen, source.PreferUnpaved)), expires);
        return new(preview, source with { DrawnRouteId = preview.Id, IsEditedRoute = true }, points, isLoop);
    }

    public RouteCandidate Save(RouteRequest request)
    {
        var errors = RequestValidation.Check(request, options);
        if (request.DrawnRouteId is not { } id || store.GetDraft(id) is not { } draft || store.Get(id) is not { } preview)
            throw new RouteValidationException("This edited route has expired. Refresh its preview before saving.");
        if (errors.Count > 0) throw new RouteValidationException("Check the route inputs.", errors);
        if (draft.Activity != request.Activity || draft.Preferences != new RoutePreferences(request.PreferGreen, request.PreferUnpaved) ||
            Geo.DistanceKm(draft.Start.Requested, request.Start!) > .000001 ||
            Geo.DistanceKm(draft.LastPoint.Requested, request.End ?? request.Start!) > .000001)
            throw new RouteValidationException("The edited route belongs to different settings. Refresh its preview before saving.");
        var range = RouteDistanceRange.For(request, options);
        var outsideRadius = request.RadiusKm is { } radius && preview.MaxDistanceFromStartKm > radius + .000001;
        var status = outsideRadius ? CandidateStatus.Diagnostic : range.Contains(preview.DistanceKm) ? CandidateStatus.Qualified : CandidateStatus.BestEffort;
        var warnings = preview.Warnings.Where(w => w.Code is not ("radiusExceeded" or "distanceMiss" or "fewerRoutes" or "partialSearch")).ToList();
        if (outsideRadius) warnings.Add(new("radiusExceeded", "The edited route exceeds your maximum radius."));
        if (!range.Contains(preview.DistanceKm)) warnings.Add(new("distanceMiss", "The edited route is outside your target distance range."));
        var candidate = preview with { Id = Guid.NewGuid(), Status = status, Warnings = warnings.ToArray(), Score = 0 };
        store.Put(candidate);
        return candidate;
    }
}
