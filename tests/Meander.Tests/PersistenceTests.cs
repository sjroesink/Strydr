using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meander.Core;
using Meander.Infrastructure;

namespace Meander.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task SearchAndGpxSurviveApiRestartWithoutCallingProvider()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meander-{Guid.NewGuid():N}.sqlite");
        var settings = new Dictionary<string, string?> { ["ResultStorage:DatabasePath"] = path };
        Guid searchId;
        Guid candidateId;
        string geometry;
        double woodlandPercent;
        try
        {
            await using (var app = new ApiFactory(new(), settings, new FixedWoodland(WoodlandTests.ForestNearStart())))
            {
                using var client = app.CreateClient();
                var response = await client.PostAsJsonAsync("/api/routes/generate", new {
                    start = Fixtures.Start, targetDistanceKm = 5, activity = "running", preferGreen = true
                });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var routes = await response.Content.ReadFromJsonAsync<JsonElement>();
                searchId = routes[0].GetProperty("searchId").GetGuid();
                candidateId = routes[0].GetProperty("id").GetGuid();
                geometry = routes[0].GetProperty("geometry").GetRawText();
                woodlandPercent = routes[0].GetProperty("woodlandPercent").GetDouble();
                Assert.True(woodlandPercent > 0);
                Assert.All(routes.EnumerateArray(), route =>
                {
                    Assert.Equal(searchId, route.GetProperty("searchId").GetGuid());
                    Assert.Equal(JsonValueKind.Null, route.GetProperty("expiresAt").ValueKind);
                });
            }
            var upstream = new StubOrs { FailureStatus = HttpStatusCode.InternalServerError };
            await using (var app = new ApiFactory(upstream, settings))
            {
                using var client = app.CreateClient();
                var saved = await client.GetFromJsonAsync<JsonElement>($"/api/searches/{searchId}");
                Assert.Equal(5, saved.GetProperty("request").GetProperty("targetDistanceKm").GetDouble());
                Assert.Equal(geometry, saved.GetProperty("candidates")[0].GetProperty("geometry").GetRawText());
                Assert.Equal(woodlandPercent, saved.GetProperty("candidates")[0].GetProperty("woodlandPercent").GetDouble());
                var gpx = await client.GetAsync($"/api/routes/{candidateId}/gpx");
                Assert.Equal(HttpStatusCode.OK, gpx.StatusCode);
                Assert.Contains("<trkpt", await gpx.Content.ReadAsStringAsync());
                Assert.Empty(upstream.Requests);
            }
        }
        finally { await DeleteDatabaseAsync(path); }
    }

    [Fact]
    public async Task SavedRidesKeepTheirActivityAfterRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meander-{Guid.NewGuid():N}.sqlite");
        var settings = new Dictionary<string, string?> { ["ResultStorage:DatabasePath"] = path };
        try
        {
            JsonElement route;
            await using (var app = new ApiFactory(new(), settings))
            {
                using var client = app.CreateClient();
                using var response = await client.PostAsJsonAsync("/api/routes/generate", new {
                    start = Fixtures.Start, targetDistanceKm = 5, activity = "cycling"
                });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                route = (await response.Content.ReadFromJsonAsync<JsonElement>())[0].Clone();
            }
            await using (var app = new ApiFactory(new() { FailureStatus = HttpStatusCode.InternalServerError }, settings))
            {
                using var client = app.CreateClient();
                var saved = await client.GetFromJsonAsync<JsonElement>($"/api/searches/{route.GetProperty("searchId").GetGuid()}");
                Assert.Equal("cycling", saved.GetProperty("request").GetProperty("activity").GetString());
                var fit = await client.GetByteArrayAsync($"/api/routes/{route.GetProperty("id").GetGuid()}/fit");
                Assert.Equal(Dynastream.Fit.Sport.Cycling, FitSport(fit));
            }
        }
        finally { await DeleteDatabaseAsync(path); }
    }

    private static Dynastream.Fit.Sport? FitSport(byte[] fit)
    {
        var decode = new Dynastream.Fit.Decode();
        var broadcaster = new Dynastream.Fit.MesgBroadcaster();
        decode.MesgEvent += broadcaster.OnMesg;
        Dynastream.Fit.Sport? sport = null;
        broadcaster.CourseMesgEvent += (_, e) => sport = ((Dynastream.Fit.CourseMesg)e.mesg).GetSport();
        using var stream = new MemoryStream(fit);
        decode.Read(stream);
        return sport;
    }

    [Fact]
    public async Task SavedDrawingAndQualitySurviveExpiryAndCanBeReusedAfterRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meander-{Guid.NewGuid():N}.sqlite");
        var clock = new TestClock();
        var options = new ResultStorageOptions { DatabasePath = path };
        SavedRouteSearch saved;
        RouteCandidate preview;
        Location[] points = [Geo.Destination(Fixtures.Start, 90, .5)];
        try
        {
            using (var storage = new SqliteRouteSearchStore(options, clock))
            using (var cache = new CandidateStore(clock, storage))
            {
                var provider = new FakeRouting
                {
                    Via = (q, _) => Task.FromResult(new RouteResult(q.Points, .5, 2, SegmentQualities: [new(.9, true)]))
                };
                preview = await new RouteGenerator(provider, cache, new(), clock)
                    .PreviewAsync(new(Fixtures.Start, points, PreferGreen: true, PreferUnpaved: true), CancellationToken.None);
                var request = Fixtures.Loop() with { DrawnRouteId = preview.Id, PreferGreen = true, PreferUnpaved = true };
                saved = storage.Save(request, [Fixtures.Candidate()], cache.GetDraft(preview.Id), preview);
                // Re-saving the same prefix must leave all older search links intact.
                storage.Save(request with { TargetDistanceKm = 6 }, [Fixtures.Candidate()], cache.GetDraft(preview.Id), preview);
            }
            clock.Now = clock.Now.AddDays(7);
            using (var storage = new SqliteRouteSearchStore(options, clock))
            using (var cache = new CandidateStore(clock, storage))
            {
                var reopened = storage.Get(saved.Id)!;
                Assert.Equal(5, reopened.Request.TargetDistanceKm);
                Assert.Equal(points, reopened.DrawnPoints);
                Assert.Null(reopened.DrawnPreview!.ExpiresAt);
                Assert.Equal(preview.Id, reopened.DrawnPreview.Id);
                var draft = cache.GetDraft(preview.Id)!;
                Assert.True(draft.Route.SegmentQualities![0].Unpaved);
                Assert.Equal(.9, draft.Route.SegmentQualities[0].GreenScore);
                Assert.NotNull(cache.Get(preview.Id));
                var provider = new FakeRouting();
                var routes = await new RouteGenerator(provider, cache, new(), clock)
                    .GenerateAsync(reopened.Request, CancellationToken.None);
                Assert.NotEmpty(routes);
                Assert.All(routes, route => Assert.Equal(preview.Geometry.Coordinates[1], route.Geometry.Coordinates[1]));
                Assert.All(provider.ViaQueries, query => Assert.Equal(draft.Route.Geometry[^1], query.Points[0]));
            }
        }
        finally { await DeleteDatabaseAsync(path); }
    }

    private static async Task DeleteDatabaseAsync(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            for (var attempt = 0; ; attempt++)
                try { File.Delete(file); break; }
                catch (IOException) when (attempt < 5) { await Task.Delay(50 * (attempt + 1)); }
    }
}
