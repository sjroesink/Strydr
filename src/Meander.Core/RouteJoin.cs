namespace Meander.Core;

public static class RouteJoin
{
    public static RouteResult Append(RouteResult prefix, RouteResult continuation)
    {
        if (Geo.DistanceKm(prefix.Geometry[^1], continuation.Geometry[0]) > .001)
            throw new ProviderException(ProviderFailure.InvalidResponse, "The continuation does not connect to the drawn route.");
        var geometry = prefix.Geometry.Concat(continuation.Geometry.Skip(1)).ToArray();
        static IEnumerable<SegmentQuality> Qualities(RouteResult route) =>
            route.SegmentQualities ?? Enumerable.Repeat(new SegmentQuality(), route.Geometry.Count - 1);
        return new(geometry, prefix.DistanceKm + continuation.DistanceKm,
            prefix.ElevationGainM is { } first && continuation.ElevationGainM is { } second ? first + second : null,
            (prefix.Warnings ?? []).Concat(continuation.Warnings ?? []).ToArray(),
            Qualities(prefix).Concat(Qualities(continuation)).ToArray());
    }
}
