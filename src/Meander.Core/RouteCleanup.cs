namespace Meander.Core;

/// <summary>
/// Cancels immediately reversed path segments, including a reversal partway
/// along an edge. It never connects unrelated roads or removes a genuine loop.
/// </summary>
public static class RouteCleanup
{
    private const double CoincidentKm = .000001; // 1 mm: avoid altering near-parallel paths.
    private const double SamePathKm = .001; // 1 m accommodates provider coordinate rounding.
    private sealed record Vertex(Location Point, SegmentQuality? IncomingQuality);

    public static RouteResult RemoveBacktracking(RouteResult route)
    {
        if (route.Geometry.Count < 3) return route;
        var stack = new List<Vertex> { new(route.Geometry[0], null) };
        var removed = false;
        for (var i = 1; i < route.Geometry.Count; i++)
        {
            var current = route.Geometry[i];
            var quality = route.SegmentQualities is { } qualities && i - 1 < qualities.Count ? qualities[i - 1] : null;
            var consumed = false;
            while (stack.Count >= 2)
            {
                var a = stack[^2].Point;
                var b = stack[^1].Point;
                if (Geo.DistanceKm(b, current) <= CoincidentKm) { consumed = true; break; }

                // Only an incoming segment traversing the previous edge backwards
                // qualifies. A repeated junction after a circuit is not a spur.
                if (OnSegment(current, a, b))
                {
                    var retainedQuality = stack[^1].IncomingQuality;
                    stack.RemoveAt(stack.Count - 1);
                    if (Geo.DistanceKm(a, current) > CoincidentKm) stack.Add(new(current, retainedQuality));
                    removed = consumed = true;
                    break;
                }
                if (OnSegment(a, b, current))
                {
                    stack.RemoveAt(stack.Count - 1);
                    removed = true;
                    continue;
                }
                break;
            }
            if (!consumed && Geo.DistanceKm(stack[^1].Point, current) > CoincidentKm)
                stack.Add(new(current, quality));
        }
        if (!removed) return route;
        var originalLength = Length(route.Geometry);
        var geometry = stack.Select(v => v.Point).ToArray();
        var retainedLength = Length(geometry);
        if (geometry.Length < 2 || retainedLength < .01)
            throw new ProviderException(ProviderFailure.NoRoute, "This route consists entirely of an out-and-back section.");
        // Preserve the provider's distance calibration rather than mixing two
        // measurement methods. Every retained edge was part of the original path.
        var distance = route.DistanceKm * retainedLength / originalLength;
        double? ascent = geometry.All(p => p.ElevationM.HasValue)
            ? geometry.Zip(geometry.Skip(1), (a, b) => Math.Max(0, b.ElevationM!.Value - a.ElevationM!.Value)).Sum() : null;
        var warnings = new List<RouteWarning>(route.Warnings ?? []);
        warnings.Add(new("backtrackingRemoved", $"Removed {route.DistanceKm - distance:F2} km of out-and-back detours. Distance and elevation have been updated."));
        return route with
        {
            Geometry = geometry, DistanceKm = distance, ElevationGainM = ascent, Warnings = warnings,
            SegmentQualities = route.SegmentQualities is null ? null
                : stack.Skip(1).Select(v => v.IncomingQuality ?? new()).ToArray()
        };
    }

    private static bool OnSegment(Location point, Location from, Location to)
    {
        var length = Geo.DistanceKm(from, to);
        if (length <= CoincidentKm) return false;
        var fromDistance = Geo.DistanceKm(from, point);
        var toDistance = Geo.DistanceKm(to, point);
        if (fromDistance <= CoincidentKm || toDistance <= CoincidentKm) return true;
        var t = (fromDistance * fromDistance + length * length - toDistance * toDistance) / (2 * length * length);
        if (t <= 0 || t >= 1) return false;
        return Geo.DistanceKm(point, Geo.Interpolate(from, to, t)) <= SamePathKm;
    }

    private static double Length(IReadOnlyList<Location> points) => points.Zip(points.Skip(1), Geo.DistanceKm).Sum();
}

public sealed record RouteQuality(double? GreenScore, double? UnpavedPercent, double? SurfaceKnownPercent)
{
    public static RouteQuality Measure(RouteResult route)
    {
        if (route.SegmentQualities is not { } qualities || qualities.Count != route.Geometry.Count - 1)
            return new(null, null, null);
        double total = 0, greenLength = 0, green = 0, surfaceLength = 0, unpavedLength = 0;
        for (var i = 0; i < qualities.Count; i++)
        {
            var length = Geo.DistanceKm(route.Geometry[i], route.Geometry[i + 1]);
            total += length;
            if (qualities[i].GreenScore is { } score)
            {
                greenLength += length;
                green += length * Math.Clamp(score, 0, 1);
            }
            if (qualities[i].Unpaved is { } unpaved)
            {
                surfaceLength += length;
                if (unpaved) unpavedLength += length;
            }
        }
        if (total <= 0) return new(null, null, null);
        // Do not present a whole-route green index from fragmentary coverage.
        return new(greenLength / total >= .9 ? green / greenLength : null,
            surfaceLength > 0 ? unpavedLength / total * 100 : null,
            surfaceLength > 0 ? surfaceLength / total * 100 : null);
    }

    public double Penalty(RoutePreferences preferences)
    {
        var count = (preferences.PreferGreen ? 1 : 0) + (preferences.PreferUnpaved ? 1 : 0);
        if (count == 0) return 0;
        return ((preferences.PreferGreen ? 1 - (GreenScore ?? 0) : 0) +
            (preferences.PreferUnpaved ? 1 - (UnpavedPercent ?? 0) / 100 : 0)) / count;
    }
}
