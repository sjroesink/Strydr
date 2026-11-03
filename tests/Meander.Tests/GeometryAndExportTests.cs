using System.Globalization;
using System.Xml.Linq;
using Meander.Core;
using Meander.Infrastructure;

namespace Meander.Tests;

public sealed class GeometryAndExportTests
{
    [Fact]
    public void HaversineWorksAcrossDatelineAndDestinationRoundTrips()
    {
        Assert.InRange(Geo.DistanceKm(new(0, 179.99), new(0, -179.99)), 2.22, 2.23);
        var point = Geo.Destination(new(89.9, 179.9), 90, 3);
        Assert.True(Geo.IsValid(point));
        Assert.InRange(Geo.DistanceKm(new(89.9, 179.9), point), 2.999, 3.001);
    }

    [Fact]
    public void ReversedAndDensifiedRoutesAreDuplicates()
    {
        var points = Fixtures.Petal().Geometry;
        var shape = new RouteShape(points);
        Assert.True(shape.Overlap(new(points.Reverse().ToArray())) > .99);
        Assert.True(shape.Overlap(new(Geo.Sample(points, .01))) > .99);
        Assert.True(shape.Overlap(new(Fixtures.Petal(180).Geometry)) < .1);
    }

    [Fact]
    public void OutAndBackHasHigherRetracingPenaltyThanLoop()
    {
        var outAndBack = new RouteShape([Fixtures.Start, Geo.Destination(Fixtures.Start, 90, 1), Fixtures.Start]);
        var loop = new RouteShape(Fixtures.Petal().Geometry);
        Assert.InRange(outAndBack.RetracingFraction, .4, .6);
        Assert.True(outAndBack.RetracingFraction > loop.RetracingFraction + .3);
    }

    [Fact]
    public void GpxUsesInvariantCoordinatesOptionalElevationAndEscapedWarnings()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
            var candidate = Fixtures.Candidate() with { Warnings = [new("test", "A < B & C")] };
            var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(GpxWriter.Write(candidate)));
            XNamespace ns = "http://www.topografix.com/GPX/1/1";
            Assert.Equal("1.1", xml.Root!.Attribute("version")!.Value);
            var points = xml.Descendants(ns + "trkpt").ToArray();
            Assert.Equal(3, points.Length);
            Assert.Equal("52.09", points[0].Attribute("lat")!.Value);
            Assert.Equal(5.12, double.Parse(points[0].Attribute("lon")!.Value, CultureInfo.InvariantCulture));
            Assert.Equal("12.5", points[0].Element(ns + "ele")!.Value);
            Assert.Null(points[1].Element(ns + "ele"));
            Assert.Contains(xml.Descendants(ns + "desc"), e => e.Value == "A < B & C");
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void CandidateExpiresAndMissingIdReturnsNull()
    {
        var clock = new TestClock();
        using var store = new CandidateStore(clock);
        var candidate = Fixtures.Candidate(clock.Now.AddMinutes(30));
        store.Put(candidate);
        Assert.Equal(candidate, store.Get(candidate.Id));
        Assert.Null(store.Get(Guid.NewGuid()));
        clock.Now = clock.Now.AddMinutes(31);
        Assert.Null(store.Get(candidate.Id));
    }
}
