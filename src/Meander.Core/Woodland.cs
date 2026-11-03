using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Index.Strtree;

namespace Meander.Core;

public interface IWoodlandProvider
{
    // Null means unavailable. An empty map means the lookup succeeded but found no woodland.
    Task<WoodlandMap?> FindAsync(Location center, double radiusKm, CancellationToken cancellationToken);
}

public sealed record WoodlandPolygon(IReadOnlyList<Location> Outer, IReadOnlyList<IReadOnlyList<Location>> Holes);
public sealed record WoodlandMeasure(double Percent, double? FirstEntryKm);

public sealed class WoodlandMap
{
    private static readonly GeometryFactory Factory = new();
    private readonly STRtree<IPreparedGeometry> index = new();
    private readonly Location center;
    private readonly Location[] pathPoints;
    public int PolygonCount { get; }

    public WoodlandMap(Location center, IReadOnlyList<WoodlandPolygon> polygons, IReadOnlyList<Location> paths)
    {
        this.center = center;
        foreach (var polygon in polygons)
        {
            try
            {
                var shell = Factory.CreateLinearRing(polygon.Outer.Select(Coordinate).ToArray());
                var holes = polygon.Holes.Select(h => Factory.CreateLinearRing(h.Select(Coordinate).ToArray())).ToArray();
                var geometry = GeometryFixer.Fix(Factory.CreatePolygon(shell, holes));
                if (geometry.IsEmpty || geometry.Area <= 0) continue;
                index.Insert(geometry.EnvelopeInternal, PreparedGeometryFactory.Prepare(geometry));
                PolygonCount++;
            }
            catch (ArgumentException) { /* Incomplete OSM rings are never treated as filled woodland. */ }
        }
        index.Build();
        pathPoints = paths.Where(Contains).DistinctBy(p => (Math.Round(p.Latitude, 5), Math.Round(p.Longitude, 5))).ToArray();
    }

    private Coordinate Coordinate(Location p) => new(center.Longitude + ((p.Longitude - center.Longitude + 540) % 360) - 180, p.Latitude);

    public bool Contains(Location location)
    {
        var point = Factory.CreatePoint(Coordinate(location));
        return index.Query(point.EnvelopeInternal).Any(polygon => polygon.Covers(point));
    }

    public WoodlandMeasure Measure(IReadOnlyList<Location> geometry)
    {
        double total = 0, woodland = 0;
        double? firstEntry = null;
        for (var i = 1; i < geometry.Count; i++)
        {
            var length = Geo.DistanceKm(geometry[i - 1], geometry[i]);
            var steps = Math.Max(1, (int)Math.Ceiling(length / .025));
            var stepLength = length / steps;
            for (var j = 0; j < steps; j++)
            {
                if (Contains(Geo.Interpolate(geometry[i - 1], geometry[i], (j + .5) / steps)))
                {
                    woodland += stepLength;
                    firstEntry ??= total;
                }
                total += stepLength;
            }
        }
        return new(total > 0 ? woodland / total * 100 : 0, firstEntry);
    }

    // Cluster nearby woodland path samples, including adjacent forest parcels separated
    // by a road. Waypoints are placed on mapped paths rather than in arbitrary trees.
    public IReadOnlyList<WoodlandTarget> Targets(Location start, Location end, double maxDistanceKm,
        Location radiusCenter, double? radiusKm)
    {
        var points = pathPoints.Where(p => Geo.DistanceKm(start, p) + Geo.DistanceKm(p, end) < maxDistanceKm * .95 &&
            (radiusKm is null || Geo.DistanceKm(radiusCenter, p) <= radiusKm * .98)).ToArray();
        var parent = Enumerable.Range(0, points.Length).ToArray();
        int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        (int, int) Cell(Location point)
        {
            var distance = Geo.DistanceKm(center, point);
            var bearing = Geo.Bearing(center, point) * Math.PI / 180;
            return ((int)Math.Floor(distance * Math.Sin(bearing) / .3), (int)Math.Floor(distance * Math.Cos(bearing) / .3));
        }
        var buckets = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < points.Length; i++)
        {
            var (x, y) = Cell(points[i]);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (buckets.TryGetValue((x + dx, y + dy), out var near))
                    foreach (var j in near)
                        if (Geo.DistanceKm(points[i], points[j]) <= .3) parent[Root(i)] = Root(j);
            if (!buckets.TryGetValue((x, y), out var bucket)) buckets[(x, y)] = bucket = [];
            bucket.Add(i);
        }
        return Enumerable.Range(0, points.Length).GroupBy(Root)
            .Select(group => new WoodlandTarget(group.Select(i => points[i]).ToArray()))
            .Where(target => target.Points.Count >= 4 && target.SpreadKm >= .15)
            .OrderByDescending(target => target.Priority(start, end, maxDistanceKm)).Take(3).ToArray();
    }
}

public sealed class WoodlandTarget(IReadOnlyList<Location> points)
{
    public IReadOnlyList<Location> Points { get; } = points;
    public double SpreadKm { get; } = points.Max(p => Geo.DistanceKm(points[0], p));
    public double Priority(Location start, Location end, double distanceKm)
    {
        var access = Points.Min(p => Geo.DistanceKm(start, p) + Geo.DistanceKm(p, end));
        return Math.Min(SpreadKm * 3, Math.Max(0, distanceKm - access * 1.25)) / (1 + access * .25);
    }

    public IReadOnlyList<Location> Waypoints(Location start, Location end, double distanceKm, int variant, double scale = 1)
    {
        var entry = Points.MinBy(p => Geo.DistanceKm(start, p))!;
        var exit = Points.MinBy(p => Geo.DistanceKm(end, p))!;
        var access = Geo.DistanceKm(start, entry) + Geo.DistanceKm(exit, end);
        var extent = Math.Clamp((distanceKm - access * 1.25) / 3 * scale, .1, 8);
        var available = Points.Where(p => Geo.DistanceKm(entry, p) <= extent).ToArray();
        if (available.Length < 3) available = Points.OrderBy(p => Geo.DistanceKm(entry, p)).Take(6).ToArray();
        // Choose spread-out path anchors. Vary the sweep to explore multiple circuits.
        var far = available.MaxBy(p => Geo.DistanceKm(entry, p))!;
        var axis = Geo.Bearing(entry, far);
        var desired = Geo.Destination(entry, axis + (variant % 2 == 0 ? 65 : -65), extent * .7);
        var other = available.OrderBy(p => Geo.DistanceKm(p, desired))
            .FirstOrDefault(p => Geo.DistanceKm(p, entry) > .08 && Geo.DistanceKm(p, far) > .08) ?? far;
        Location[] anchors = variant % 2 == 0 ? [entry, far, other, exit] : [entry, other, far, exit];
        var result = new List<Location> { start };
        foreach (var point in anchors)
            if (Geo.DistanceKm(result[^1], point) > .02) result.Add(point);
        if (Geo.DistanceKm(result[^1], end) > .001) result.Add(end);
        return result;
    }
}
