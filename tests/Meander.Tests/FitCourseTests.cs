using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dynastream.Fit;
using Meander.Core;
using Activity = Meander.Core.Activity;
using Meander.Infrastructure;

namespace Meander.Tests;

public sealed class FitCourseTests
{
    private static (FileIdMesg? FileId, CourseMesg? Course, LapMesg? Lap, List<RecordMesg> Records) Decode(byte[] bytes)
    {
        var decode = new Decode();
        var broadcaster = new MesgBroadcaster();
        decode.MesgEvent += broadcaster.OnMesg;
        FileIdMesg? fileId = null; CourseMesg? course = null; LapMesg? lap = null;
        var records = new List<RecordMesg>();
        broadcaster.FileIdMesgEvent += (_, e) => fileId = (FileIdMesg)e.mesg;
        broadcaster.CourseMesgEvent += (_, e) => course = (CourseMesg)e.mesg;
        broadcaster.LapMesgEvent += (_, e) => lap = (LapMesg)e.mesg;
        broadcaster.RecordMesgEvent += (_, e) => records.Add((RecordMesg)e.mesg);
        using var stream = new MemoryStream(bytes);
        Assert.True(decode.CheckIntegrity(stream));
        stream.Position = 0;
        Assert.True(decode.Read(stream));
        return (fileId, course, lap, records);
    }

    [Fact]
    public void WritesADecodableCourseWithOneRecordPerPoint()
    {
        var route = Fixtures.Candidate();
        var (fileId, course, lap, records) = Decode(FitCourseWriter.Write(route, Activity.Walking, DateTimeOffset.UnixEpoch.AddYears(56)));
        Assert.Equal(Dynastream.Fit.File.Course, fileId!.GetType());
        Assert.Equal(Sport.Walking, course!.GetSport());
        Assert.Equal("Meander 2.0 km", course.GetNameAsString());
        Assert.Equal(route.Geometry.Coordinates.Length, records.Count);
        const double toDegrees = 180d / 2147483648d;
        Assert.Equal(52.09, records[0].GetPositionLat()!.Value * toDegrees, 5);
        Assert.Equal(5.12, records[0].GetPositionLong()!.Value * toDegrees, 5);
        Assert.Equal(12.5, records[0].GetEnhancedAltitude()!.Value, 0.2); // FIT stores altitude in 0.2 m steps
        Assert.Equal(0, records[0].GetDistance()!.Value, 1);
        // Two 1 km legs out and back along a straight line.
        Assert.Equal(2000, records[^1].GetDistance()!.Value, 5.0);
        Assert.Equal(records[^1].GetDistance()!.Value, lap!.GetTotalDistance()!.Value, 0.5);
        Assert.True(records[^1].GetTimestamp().GetTimeStamp() > records[0].GetTimestamp().GetTimeStamp());
    }

    [Fact]
    public void RunningRoutesUseTheRunningSport()
    {
        var (_, course, _, _) = Decode(FitCourseWriter.Write(Fixtures.Candidate(), Activity.Running, DateTimeOffset.UtcNow));
        Assert.Equal(Sport.Running, course!.GetSport());
    }

    [Fact]
    public void CyclingRoutesUseTheCyclingSportAndARidingPace()
    {
        var (_, course, lap, records) = Decode(FitCourseWriter.Write(Fixtures.Candidate(), Activity.Cycling, DateTimeOffset.UtcNow));
        Assert.Equal(Sport.Cycling, course!.GetSport());
        // 20 km/h: 0.18 s per metre, against 0.36 on foot.
        Assert.Equal(lap!.GetTotalDistance()!.Value * .18, lap.GetTotalTimerTime()!.Value, 1.0);
        var (_, _, footLap, _) = Decode(FitCourseWriter.Write(Fixtures.Candidate(), Activity.Running, DateTimeOffset.UtcNow));
        Assert.Equal(footLap!.GetTotalDistance()!.Value * .36, footLap.GetTotalTimerTime()!.Value, 1.0);
        Assert.NotEmpty(records);
    }

    [Fact]
    public async Task FitEndpointServesGeneratedAndSavedRoutes()
    {
        await using var app = new ApiFactory(new());
        using var client = app.CreateClient();
        var generated = await client.PostAsJsonAsync("/api/routes/generate",
            new { start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 5, activity = "running" });
        var id = (await generated.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetString();
        using var response = await client.GetAsync($"/api/routes/{id}/fit");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.ant.fit", response.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".fit", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.NotEmpty(Decode(await response.Content.ReadAsByteArrayAsync()).Records);

        using var missing = await client.GetAsync($"/api/routes/{Guid.NewGuid()}/fit");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("exportExpired", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
