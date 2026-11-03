using System.Xml.Linq;
using Meander.Core;
using Meander.Infrastructure;

namespace Meander.Tests;

public sealed class RouteCleanupTests
{
    [Fact]
    public void RemovesBentInternalSpurAndUpdatesExportedGeometryDistanceElevationAndQualities()
    {
        var start = Fixtures.Start with { ElevationM = 10 };
        var junction = Geo.Destination(start, 90, 1) with { ElevationM = 10 };
        var bend = Geo.Destination(junction, 0, .2) with { ElevationM = 20 };
        var tip = Geo.Destination(bend, 90, .1) with { ElevationM = 30 };
        var onward = Geo.Destination(junction, 0, 1) with { ElevationM = 15 };
        Location[] geometry = [start, junction, bend, tip, bend, junction, onward, start];
        var originalLength = geometry.Zip(geometry.Skip(1), Geo.DistanceKm).Sum();
        var qualities = Enumerable.Range(0, 7).Select(i => new SegmentQuality(.5, i is >= 1 and <= 4)).ToArray();
        var route = RouteCleanup.RemoveBacktracking(new(geometry, originalLength, 25, SegmentQualities: qualities));
        Assert.Equal(new[] { start, junction, onward, start }, route.Geometry);
        Assert.InRange(originalLength - route.DistanceKm, .599, .601);
        Assert.Equal(5, route.ElevationGainM);
        Assert.All(route.SegmentQualities!, q => Assert.False(q.Unpaved));
        Assert.Equal(0, RouteQuality.Measure(route).UnpavedPercent);
        Assert.Contains(route.Warnings!, w => w.Code == "backtrackingRemoved");
        var candidate = Fixtures.Candidate() with { Geometry = LineString.From(route.Geometry), DistanceKm = route.DistanceKm };
        using var stream = new MemoryStream(GpxWriter.Write(candidate));
        var gpx = XDocument.Load(stream);
        Assert.Equal(4, gpx.Descendants(XName.Get("trkpt", "http://www.topografix.com/GPX/1/1")).Count());
    }

    [Fact]
    public void HandlesTurnaroundInsideAnEdgeWithUnequalVertexSpacing()
    {
        var start = Fixtures.Start;
        var junction = Geo.Destination(start, 90, 1);
        var tip = Geo.Destination(junction, 0, .2);
        var returnedPartway = Geo.Interpolate(junction, tip, .4);
        var onward = Geo.Destination(junction, 330, 1);
        var route = RouteCleanup.RemoveBacktracking(new([start, junction, tip, returnedPartway, junction, onward, start], 4, null));
        Assert.Equal(new[] { start, junction, onward, start }, route.Geometry);
        Assert.True(route.DistanceKm < 4);
    }

    [Fact]
    public void PreservesGenuineLoopsAndSharedAccessStem()
    {
        var start = Fixtures.Start;
        var junction = Geo.Destination(start, 0, .2);
        var left = Geo.Destination(junction, 300, 1);
        var right = Geo.Destination(junction, 60, 1);
        var original = new RouteResult([start, junction, left, right, junction, start], 4, 20);
        Assert.Same(original, RouteCleanup.RemoveBacktracking(original));
    }

    [Fact]
    public void DoesNotJoinNearbyParallelPaths()
    {
        var start = Fixtures.Start;
        var a = Geo.Destination(start, 90, 1);
        var b = Geo.Destination(a, 0, .2);
        var c = Geo.Destination(b, 90, .005);
        var d = Geo.Destination(a, 90, .005);
        var onward = Geo.Destination(d, 130, .5);
        var original = new RouteResult([start, a, b, c, d, onward, start], 4, null);
        Assert.Same(original, RouteCleanup.RemoveBacktracking(original));
    }

    [Fact]
    public void RejectsEntirelyOutAndBackLoopInsteadOfInventingReplacementGeometry()
    {
        var error = Assert.Throws<ProviderException>(() => RouteCleanup.RemoveBacktracking(
            new([Fixtures.Start, Geo.Destination(Fixtures.Start, 90, 1), Fixtures.Start], 2, 0)));
        Assert.Equal(ProviderFailure.NoRoute, error.Failure);
    }

    [Fact]
    public void QualityUsesLengthAndKeepsUnknownSurfacesUnknown()
    {
        var start = Fixtures.Start;
        var middle = Geo.Destination(start, 0, 1);
        var end = Geo.Destination(middle, 0, 1);
        var route = new RouteResult([start, middle, end], 2, 0,
            SegmentQualities: [new(.8, true), new(.4, null)]);
        var quality = RouteQuality.Measure(route);
        Assert.InRange(quality.GreenScore!.Value, .599, .601);
        Assert.InRange(quality.UnpavedPercent!.Value, 49.99, 50.01);
        Assert.InRange(quality.SurfaceKnownPercent!.Value, 49.99, 50.01);
        Assert.Null(RouteQuality.Measure(route with { SegmentQualities = [new(.8, true), new(null, null)] }).GreenScore);
    }
}
