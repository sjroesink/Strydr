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
    private readonly STRtree<Location> pathIndex = new();
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
        foreach (var point in pathPoints) pathIndex.Insert(new Envelope(Coordinate(point)), point);
        pathIndex.Build();
    }

    private Coordinate Coordinate(Location p) => new(center.Longitude + ((p.Longitude - center.Longitude + 540) % 360) - 180, p.Latitude);

    // Mapped forest is often split along the paths that cross it (common in imported Dutch data),
    // which leaves a forest path just outside every polygon. Points this close still count.
    private const double EdgeToleranceDegrees = 20 / 111_320.0;

    public bool Contains(Location location)
    {
        var point = Factory.CreatePoint(Coordinate(location));
        if (index.Query(point.EnvelopeInternal).Any(polygon => polygon.Covers(point))) return true;
        var near = new Envelope(point.Coordinate);
        near.ExpandBy(EdgeToleranceDegrees);
        return index.Query(near).Any(polygon => polygon.Geometry.IsWithinDistance(point, EdgeToleranceDegrees));
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
        Location radiusCenter, double? radiusKm) =>
        Clusters(start, end, maxDistanceKm, radiusCenter, radiusKm)
            .OrderByDescending(target => target.Priority(start, end, maxDistanceKm)).Take(3).ToArray();

    // Loops from the start as ovals of the requested length, ranked by how much of the line
    // through their anchors runs in woodland. An oval can string several forest blocks together,
    // which a circuit inside a single block cannot do once the route outgrows that block.
    // With a home to return to afterwards, only ovals on the side away from home qualify.
    public IReadOnlyList<WoodlandLoop> Loops(Location start, double distanceKm, Location radiusCenter, double? radiusKm,
        Location? home = null)
    {
        var away = home is { } h && Geo.DistanceKm(h, start) > .05 ? Geo.Bearing(h, start) : (double?)null;
        var ranked = Enumerable.Range(0, 16).Select(i => i * 22.5)
            .Where(bearing => away is null || AngleBetween(bearing, away.Value) <= 90)
            .SelectMany(bearing => WoodlandLoop.Shapes.Select(stretch => new WoodlandLoop(this, start, bearing, stretch)))
            .Select(loop => (Loop: loop, Points: loop.Waypoints(distanceKm, 1, 1, point => point)))
            .Where(c => radiusKm is null || c.Points.All(p => Geo.DistanceKm(radiusCenter, p) <= radiusKm * .98))
            // Forest length rather than share, so an oval that lost most anchors does not win as a short spur.
            .Select(c => (c.Loop, Cover: Measure(c.Points).Percent / 100 * c.Points.Zip(c.Points.Skip(1), Geo.DistanceKm).Sum()))
            .Where(c => c.Cover > 0).OrderByDescending(c => c.Cover).ToArray();
        var loops = new List<WoodlandLoop>();
        // Directions that barely touch the woodland would only spend attempts on farmland.
        foreach (var (loop, _) in ranked.TakeWhile(c => c.Cover >= ranked[0].Cover / 2))
            if (loops.All(other => AngleBetween(loop.Bearing, other.Bearing) >= 30))
                loops.Add(loop);
        return loops;
    }

    private static double AngleBetween(double a, double b) => Math.Abs((a - b + 540) % 360 - 180);

    internal Location? NearestPath(Location point, double maxKm, Func<Location, bool>? allowed = null)
    {
        var near = new Envelope(Coordinate(point));
        near.ExpandBy(maxKm / 111.32 / Math.Max(.1, Math.Cos(point.Latitude * Math.PI / 180)), maxKm / 111.32);
        return pathIndex.Query(near).Where(p => allowed?.Invoke(p) ?? true)
            .Select(p => (Point: p, Distance: Geo.DistanceKm(point, p))).Where(p => p.Distance <= maxKm)
            .OrderBy(p => p.Distance).Select(p => (Location?)p.Point).FirstOrDefault();
    }

    private IEnumerable<WoodlandTarget> Clusters(Location start, Location end, double maxDistanceKm,
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
            .Where(target => target.Points.Count >= 4 && target.SpreadKm >= .15);
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

public sealed class WoodlandLoop(WoodlandMap map, Location start, double bearing, double stretch)
{
    // Semi-axis along the bearing divided by the one across it: round, long and wide ovals.
    public static readonly double[] Shapes = [1, 2, .5];
    public double Bearing { get; } = bearing;

    // Anchors around an oval through the start whose centre lies along the bearing, each moved
    // onto a nearby woodland path. Paths wind roughly a quarter beyond the oval they trace.
    // Turn picks the direction of travel; scale resizes the oval to correct a measured distance.
    // Finish replaces the return to the start when the route continues elsewhere.
    public IReadOnlyList<Location> Waypoints(double distanceKm, int turn, double scale, Func<Location, Location> clamp,
        Location? finish = null)
    {
        // Ramanujan's perimeter for semi-axes stretch*b and b.
        var h = Math.Pow(stretch - 1, 2) / Math.Pow(stretch + 1, 2);
        var across = distanceKm / 1.25 / (Math.PI * (stretch + 1) * (1 + 3 * h / (10 + Math.Sqrt(4 - 3 * h)))) * scale;
        var along = across * stretch;
        var center = Geo.Destination(start, Bearing, along);
        var snapKm = (along + across) / 2 * .75;
        var oval = Enumerable.Range(1, 5).Select(i => Math.PI + turn * i * 2 * Math.PI / 6)
            .Select(angle => Geo.Destination(Geo.Destination(center, Bearing, along * Math.Cos(angle)), Bearing + 90, across * Math.Sin(angle)))
            .ToArray();
        // An anchor without woodland nearby adds nothing; the router links its neighbours itself.
        // Before heading elsewhere, stay out of woodland nearer that destination than the start:
        // that belongs to the way there, not to this oval.
        var homeKm = finish is null ? 0 : Geo.DistanceKm(start, finish);
        var anchors = oval.Select(point => map.NearestPath(point, snapKm, p => finish is null || Geo.DistanceKm(p, finish) >= homeKm))
            .OfType<Location>().ToArray();
        var result = new List<Location> { start };
        foreach (var anchor in (anchors.Length > 0 ? anchors : oval).Select(clamp))
            if (Geo.DistanceKm(result[^1], anchor) > .02) result.Add(anchor);
        result.Add(finish ?? start);
        return result;
    }
}
