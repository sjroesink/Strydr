using System.Globalization;
using Dynastream.Fit;
using Meander.Core;
using Activity = Meander.Core.Activity;

namespace Meander.Infrastructure;

// A FIT course as Garmin devices expect it: FileId, Course, Lap, timer start, one Record per
// point, timer stop. Courses need timestamps, so points get a synthetic pace:
// 6 min/km on foot, 20 km/h by bike.
public static class FitCourseWriter
{
    private const double Semicircles = 2147483648d / 180d;

    public static byte[] Write(RouteCandidate route, Activity activity, DateTimeOffset createdAt)
    {
        var secondsPerMetre = activity == Activity.Cycling ? .18 : .36;
        var points = route.Geometry.Coordinates;
        var distances = new double[points.Length];
        for (var i = 1; i < points.Length; i++)
            distances[i] = distances[i - 1] + Geo.DistanceKm(At(points[i - 1]), At(points[i])) * 1000;
        var start = createdAt.UtcDateTime;
        Dynastream.Fit.DateTime Time(double metres) => new(start.AddSeconds(Math.Round(metres * secondsPerMetre)));
        var total = distances.Length == 0 ? 0 : distances[^1];

        using var stream = new MemoryStream();
        var encoder = new Encode(ProtocolVersion.V20);
        encoder.Open(stream);

        var fileId = new FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Course);
        fileId.SetManufacturer(Manufacturer.Development);
        fileId.SetProduct(0);
        fileId.SetSerialNumber(1);
        fileId.SetTimeCreated(Time(0));
        encoder.Write(fileId);

        var course = new CourseMesg();
        course.SetName($"Meander {route.DistanceKm.ToString("F1", CultureInfo.InvariantCulture)} km");
        course.SetSport(activity switch
        {
            Activity.Walking => Sport.Walking,
            Activity.Cycling => Sport.Cycling,
            _ => Sport.Running
        });
        encoder.Write(course);

        var lap = new LapMesg();
        lap.SetMessageIndex(0);
        lap.SetTimestamp(Time(0));
        lap.SetStartTime(Time(0));
        if (points.Length > 0)
        {
            lap.SetStartPositionLat(Lat(points[0])); lap.SetStartPositionLong(Lon(points[0]));
            lap.SetEndPositionLat(Lat(points[^1])); lap.SetEndPositionLong(Lon(points[^1]));
        }
        lap.SetTotalDistance((float)total);
        lap.SetTotalElapsedTime((float)(total * secondsPerMetre));
        lap.SetTotalTimerTime((float)(total * secondsPerMetre));
        encoder.Write(lap);

        encoder.Write(TimerEvent(Time(0), EventType.Start));
        for (var i = 0; i < points.Length; i++)
        {
            var record = new RecordMesg();
            record.SetTimestamp(Time(distances[i]));
            record.SetPositionLat(Lat(points[i]));
            record.SetPositionLong(Lon(points[i]));
            record.SetDistance((float)distances[i]);
            if (points[i].Length > 2) record.SetEnhancedAltitude((float)points[i][2]);
            encoder.Write(record);
        }
        encoder.Write(TimerEvent(Time(total), EventType.StopDisableAll));
        encoder.Close();
        return stream.ToArray();
    }

    private static EventMesg TimerEvent(Dynastream.Fit.DateTime time, EventType type)
    {
        var message = new EventMesg();
        message.SetTimestamp(time);
        message.SetEvent(Event.Timer);
        message.SetEventType(type);
        return message;
    }
    private static Location At(double[] p) => new(p[1], p[0]);
    private static int Lat(double[] p) => (int)Math.Round(p[1] * Semicircles);
    private static int Lon(double[] p) => (int)Math.Round(p[0] * Semicircles);
}
