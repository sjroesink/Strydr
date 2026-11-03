namespace Meander.Core;

public static class Geo
{
    private const double EarthKm = 6371.0088;
    private const double Rad = Math.PI / 180;
    public static bool IsValid(Location? p) => p is not null && double.IsFinite(p.Latitude) &&
        double.IsFinite(p.Longitude) && p.Latitude is >= -90 and <= 90 && p.Longitude is >= -180 and <= 180 &&
        (p.ElevationM is null || double.IsFinite(p.ElevationM.Value));

    public static double DistanceKm(Location a, Location b)
    {
        var dLat = (b.Latitude - a.Latitude) * Rad;
        var dLon = (b.Longitude - a.Longitude) * Rad;
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(a.Latitude * Rad) * Math.Cos(b.Latitude * Rad) * Math.Pow(Math.Sin(dLon / 2), 2);
        return EarthKm * 2 * Math.Asin(Math.Sqrt(Math.Clamp(h, 0, 1)));
    }

    public static double Bearing(Location a, Location b)
    {
        var dLon = (b.Longitude - a.Longitude) * Rad;
        return (Math.Atan2(Math.Sin(dLon) * Math.Cos(b.Latitude * Rad),
            Math.Cos(a.Latitude * Rad) * Math.Sin(b.Latitude * Rad) -
            Math.Sin(a.Latitude * Rad) * Math.Cos(b.Latitude * Rad) * Math.Cos(dLon)) / Rad + 360) % 360;
    }

    public static Location Destination(Location start, double bearing, double distanceKm)
    {
        var angular = distanceKm / EarthKm;
        var lat = start.Latitude * Rad;
        var b = bearing * Rad;
        var nextLat = Math.Asin(Math.Clamp(Math.Sin(lat) * Math.Cos(angular) + Math.Cos(lat) * Math.Sin(angular) * Math.Cos(b), -1, 1));
        var lon = start.Longitude * Rad + Math.Atan2(Math.Sin(b) * Math.Sin(angular) * Math.Cos(lat), Math.Cos(angular) - Math.Sin(lat) * Math.Sin(nextLat));
        return new(nextLat / Rad, ((lon / Rad + 540) % 360) - 180);
    }

    public static Location Interpolate(Location a, Location b, double fraction) =>
        Destination(a, Bearing(a, b), DistanceKm(a, b) * fraction);

    public static IReadOnlyList<Location> Sample(IReadOnlyList<Location> points, double stepKm = .02)
    {
        if (points.Count < 2) return points;
        var sampled = new List<Location> { points[0] };
        for (var i = 1; i < points.Count; i++)
        {
            var count = Math.Max(1, (int)Math.Ceiling(DistanceKm(points[i - 1], points[i]) / stepKm));
            for (var s = 1; s <= count; s++)
                sampled.Add(s == count ? points[i] : Interpolate(points[i - 1], points[i], (double)s / count));
        }
        return sampled;
    }

    public static Location Inside(Location point, Location center, double radiusKm) =>
        DistanceKm(center, point) <= radiusKm ? point : Destination(center, Bearing(center, point), radiusKm);
}

// Sampled segment midpoints and lengths avoid dependence on provider vertex density.
// The 3-D Earth grid is continuous at the dateline and works at high latitudes.
public sealed class RouteShape
{
    private const double CellKm = .02;
    private readonly Dictionary<(int, int, int), List<Segment>> buckets = [];
    private readonly List<Segment> segments = [];
    private sealed record Segment(Location Midpoint, double Length, double Along);
    public double LengthKm { get; }
    public double RetracingFraction { get; }

    public RouteShape(IReadOnlyList<Location> geometry)
    {
        var sampled = Geo.Sample(geometry);
        double length = 0, repeated = 0;
        for (var i = 1; i < sampled.Count; i++)
        {
            var len = Geo.DistanceKm(sampled[i - 1], sampled[i]);
            if (len < .000001) continue;
            var segment = new Segment(Geo.Interpolate(sampled[i - 1], sampled[i], .5), len, length + len / 2);
            if (Nearby(segment.Midpoint).Any(s => segment.Along - s.Along > .06 && Geo.DistanceKm(s.Midpoint, segment.Midpoint) <= CellKm))
                repeated += len;
            var cell = Key(segment.Midpoint);
            if (!buckets.TryGetValue(cell, out var list)) buckets[cell] = list = [];
            list.Add(segment);
            segments.Add(segment);
            length += len;
        }
        LengthKm = length;
        RetracingFraction = length > 0 ? repeated / length : 0;
    }

    public double Overlap(RouteShape other)
    {
        var shorter = LengthKm <= other.LengthKm ? this : other;
        var longer = ReferenceEquals(shorter, this) ? other : this;
        return shorter.LengthKm <= 0 ? 1 : shorter.segments.Where(s =>
            longer.Nearby(s.Midpoint).Any(t => Geo.DistanceKm(s.Midpoint, t.Midpoint) <= CellKm)).Sum(s => s.Length) / shorter.LengthKm;
    }

    private IEnumerable<Segment> Nearby(Location p)
    {
        var (x, y, z) = Key(p);
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
            if (buckets.TryGetValue((x + dx, y + dy, z + dz), out var list))
                foreach (var item in list) yield return item;
    }

    private static (int, int, int) Key(Location p)
    {
        var lat = p.Latitude * Math.PI / 180;
        var lon = p.Longitude * Math.PI / 180;
        const double scale = 6371.0088 / CellKm;
        return ((int)Math.Floor(scale * Math.Cos(lat) * Math.Cos(lon)),
            (int)Math.Floor(scale * Math.Cos(lat) * Math.Sin(lon)), (int)Math.Floor(scale * Math.Sin(lat)));
    }
}
