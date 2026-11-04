# Service Integrations (Garmin) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Visitors can download a route as a FIT course and send it to their own Garmin Connect account as a Course. A connection layer keeps OAuth separate from capabilities, so more services can be added later.

**Architecture:**
- **Core:** owns the abstractions and `ConnectionService`, which handles token refresh. The abstractions are `IServiceConnector` (OAuth only), `IRouteDestination` (push a route) and `IConnectionStore`.
- **Infrastructure:** holds the SQLite token store (encrypted with ASP.NET Data Protection), the PKCE helper, the Garmin adapters and the FIT writer.
- **API:** `ConnectionEndpoints.cs` runs the OAuth flow, keyed to an anonymous `meander_sid` cookie.
- **Frontend:** adds a FIT download, a "Send to Garmin" action, a small connections panel, and the automatic send when the visitor returns from Garmin.

**Tech Stack:**
- Backend: .NET 10 minimal API, `Microsoft.Data.Sqlite`, ASP.NET Data Protection, `Garmin.FIT.Sdk`, xUnit with `WebApplicationFactory`.
- Frontend: React 19 + TypeScript, Vitest + Testing Library, Playwright.

**Spec:** `docs/superpowers/specs/2026-11-04-service-integrations-design.md`

## Global Constraints

- **Prerequisite:** the multilingual plan (`docs/superpowers/plans/2026-11-04-multilingual.md`) is merged into `main` and this branch is rebased on it. Every ProblemDetails carries a `code` extension, and the frontend shows text through `useI18n()` (`t`, `say`, `number`) and `failureMessages`.
- Every new user-facing string is a key in `frontend/src/locales/en.ts` with a Dutch value in `nl.ts`. Every new API problem code `X` has an `errors.X` key in both files.
- Service ids in URLs and JSON are lowercase (`garmin`). C# uses `ServiceKind.Garmin`, which the existing camelCase enum converter serialises as `"garmin"`.
- Cookies:
  - `meander_sid`: random GUID, HttpOnly, SameSite=Lax, Path=/, lifetime 1 year, Secure when `PublicBaseUrl` is https.
  - `meander_oauth`: Data-Protection-protected, HttpOnly, SameSite=Lax, Path=/, lifetime 10 minutes, Secure under the same rule.
- `returnUrl` must be a local path: it starts with `/` and does not start with `//` or `/\`. Anything else becomes `/`.
- The OAuth redirect URI is `{PublicBaseUrl}/api/connections/{service}/callback`. It is never derived from request headers.
- Calls to Garmin (OAuth and the course POST) are never retried automatically, because codes are single-use and courses must not be duplicated. They have a 20-second `HttpClient.Timeout`.
- A service is enabled only when its `ClientId` is set. With no `ClientId`, `/api/connections` returns `[]` and the FIT download still works.
- Commands:
  - `dotnet test Meander.slnx` from the repo root.
  - `npm test`, `npx tsc -b` and `npm run test:e2e` from `frontend/`.
- Commit messages are imperative sentences matching the repo history, for example "Add FIT course export". End each one with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## File Structure

Backend:
- **Create:**
  - `src/Meander.Core/Connections.cs`: `ServiceKind`, `ServiceTokens`, `ServiceConnection`, `SentRoute`, `IConnectionStore`, `IServiceConnector`, `IRouteDestination`, `ConnectionService`.
  - `src/Meander.Infrastructure/FitCourseWriter.cs`
  - `src/Meander.Infrastructure/Connections/SqliteConnectionStore.cs`
  - `src/Meander.Infrastructure/Connections/OAuthPkce.cs`
  - `src/Meander.Infrastructure/Connections/GarminOptions.cs`: options plus the deregistration payload records.
  - `src/Meander.Infrastructure/Connections/GarminConnector.cs`
  - `src/Meander.Infrastructure/Connections/GarminCourseDestination.cs`: destination plus `GarminCourseMapper`.
  - `src/Meander.Api/RouteLookup.cs`: finds a route and its activity in the saved or cached stores.
  - `src/Meander.Api/ConnectionEndpoints.cs`: `ConnectionOptions`, the cookie helpers and every `/api/connections*` endpoint, plus the webhook.
- **Modify:**
  - `src/Meander.Core/Domain.cs`: `ProviderFailure.Unauthorized`.
  - `src/Meander.Api/Program.cs`: DI, options, the `connect` rate limit, the `/fit` endpoint, `MapConnectionEndpoints()`.
  - `src/Meander.Api/appsettings.json`
  - `src/Meander.Infrastructure/Meander.Infrastructure.csproj`
- **Tests:**
  - Create: `tests/Meander.Tests/FitCourseTests.cs`, `ConnectionServiceTests.cs`, `ConnectionStoreTests.cs`, `ConnectionApiTests.cs`, `GarminTests.cs`, `ConnectionFixtures.cs`.
  - Modify: `ApiTests.cs`, where `ApiFactory` gains a `configure` hook.

Frontend:
- **Create:**
  - `frontend/src/useConnections.ts`
  - `frontend/src/components/ConnectionsPanel.tsx`
  - `frontend/src/connectReturn.ts`: parses `?connected=` and `?connectError=`.
  - `frontend/src/connectReturn.test.ts`
- **Modify:** `frontend/src/api.ts`, `types.ts`, `App.tsx`, `components/Icon.tsx`, `styles.css`, `locales/en.ts`, `locales/nl.ts`, `App.test.tsx`, `api.test.ts`.
- **End-to-end:** modify `frontend/e2e/upstream.mjs`, `frontend/playwright.config.ts` and `frontend/e2e/route.spec.ts`.

Docs and deploy: `README.md`, `deploy/unraid/meander/docker-compose.yml`.

---

### Task 0: Rebase on the multilingual work

**Files:** none.

- [ ] **Step 1: Check that multilingual is on main**

Run: `git log main --oneline | grep -i "translate the ui"` and `git show main:frontend/src/App.tsx | grep -c useI18n`
Expected: one commit line, and a count of at least 1. If either is missing, stop and report that the multilingual plan has not been merged yet.

- [ ] **Step 2: Rebase**

Run: `git switch feature/service-integrations && git rebase main`
Expected: the rebase succeeds. Only the spec commit and this plan commit move.

- [ ] **Step 3: Baseline**

Run: `dotnet test Meander.slnx` and `cd frontend && npm test && npx tsc -b`
Expected: all pass.

---

### Task 1: FIT course export

**Files:**
- Create: `src/Meander.Infrastructure/FitCourseWriter.cs`
- Create: `src/Meander.Api/RouteLookup.cs`
- Modify: `src/Meander.Infrastructure/Meander.Infrastructure.csproj`
- Modify: `src/Meander.Api/Program.cs`, by adding an endpoint directly after `app.MapGet("/api/routes/{id:guid}/gpx", …)`.
- Test: `tests/Meander.Tests/FitCourseTests.cs`

**Interfaces:**
- Produces: `public static byte[] FitCourseWriter.Write(RouteCandidate route, Activity activity, DateTimeOffset createdAt)`
- Produces: `public static (RouteCandidate Route, Activity Activity)? RouteLookup.Find(Guid id, IRouteSearchStore searches, ICandidateStore cache)`
- Produces: `GET /api/routes/{id}/fit` returns `application/vnd.ant.fit` with filename `meander-{km:F1}km-{id:N}.fit`, or 404 with code `exportExpired`.

- [ ] **Step 1: Add the FIT SDK package**

Run: `dotnet add src/Meander.Infrastructure/Meander.Infrastructure.csproj package Garmin.FIT.Sdk`
Expected: a `PackageReference Include="Garmin.FIT.Sdk"` appears with the latest version.

- [ ] **Step 2: Write the failing tests** in `tests/Meander.Tests/FitCourseTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dynastream.Fit;
using Meander.Core;
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
        Assert.Equal(12.5, records[0].GetEnhancedAltitude()!.Value, 1);
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
```

- [ ] **Step 3: Run the tests and confirm they fail**

Run: `dotnet test Meander.slnx --filter FitCourseTests`
Expected: the build fails because `FitCourseWriter` does not exist.

- [ ] **Step 4: Implement `FitCourseWriter`** in `src/Meander.Infrastructure/FitCourseWriter.cs`

```csharp
using System.Globalization;
using Dynastream.Fit;
using Meander.Core;

namespace Meander.Infrastructure;

// A FIT course as Garmin devices expect it: FileId, Course, Lap, timer start, one Record per
// point, timer stop. Courses need timestamps, so points get a synthetic 6 min/km pace.
public static class FitCourseWriter
{
    private const double Semicircles = 2147483648d / 180d;
    private const double SecondsPerMetre = .36;

    public static byte[] Write(RouteCandidate route, Activity activity, DateTimeOffset createdAt)
    {
        var points = route.Geometry.Coordinates;
        var distances = new double[points.Length];
        for (var i = 1; i < points.Length; i++)
            distances[i] = distances[i - 1] + Geo.DistanceKm(At(points[i - 1]), At(points[i])) * 1000;
        var start = createdAt.UtcDateTime;
        Dynastream.Fit.DateTime Time(double metres) => new(start.AddSeconds(Math.Round(metres * SecondsPerMetre)));
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
        course.SetSport(activity == Activity.Walking ? Sport.Walking : Sport.Running);
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
        lap.SetTotalElapsedTime((float)(total * SecondsPerMetre));
        lap.SetTotalTimerTime((float)(total * SecondsPerMetre));
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
```

- [ ] **Step 5: Implement `RouteLookup`** in `src/Meander.Api/RouteLookup.cs`

```csharp
using Meander.Core;

// Saved routes carry their search (and so the activity); drawn previews only live in the cache.
public static class RouteLookup
{
    public static (RouteCandidate Route, Activity Activity)? Find(Guid id, IRouteSearchStore searches, ICandidateStore cache)
    {
        var route = searches.GetCandidate(id) ?? cache.Get(id);
        if (route is null) return null;
        var activity = route.SearchId is { } searchId ? searches.Get(searchId)?.Request.Activity
            : (cache.GetDraft(id) ?? searches.GetDraft(id))?.Activity;
        return (route, activity ?? Activity.Running);
    }
}
```

- [ ] **Step 6: Add the endpoint** to `Program.cs`, directly after the `/gpx` endpoint. Copy the 404 title, detail and `code` (`exportExpired`) from the `/gpx` endpoint's problem as it stands after the multilingual work.

```csharp
app.MapGet("/api/routes/{id:guid}/fit", (Guid id, IRouteSearchStore searches, ICandidateStore cache, TimeProvider clock, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return RouteLookup.Find(id, searches, cache) is { } found
        ? Results.File(FitCourseWriter.Write(found.Route, found.Activity, clock.GetUtcNow()), "application/vnd.ant.fit",
            $"meander-{found.Route.DistanceKm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}km-{id:N}.fit")
        : Results.Problem(statusCode: 404, title: "Route export expired or unavailable", detail: "Generate your route again to create a new download.",
            extensions: new Dictionary<string, object?> { ["code"] = "exportExpired" });
});
```

- [ ] **Step 7: Run the tests and confirm they pass**

Run: `dotnet test Meander.slnx --filter FitCourseTests`
Expected: 3 tests pass.

If `GetEnhancedAltitude` returns null, the SDK stores the value only in `altitude`. In that case, write `SetAltitude` as well and assert on `GetAltitude()`.

- [ ] **Step 8: Commit**

```bash
git add src/Meander.Infrastructure src/Meander.Api tests/Meander.Tests/FitCourseTests.cs
git commit -m "Add FIT course export"
```

---

### Task 2: Connection core and token refresh

**Files:**
- Create: `src/Meander.Core/Connections.cs`
- Modify: `src/Meander.Core/Domain.cs`, the `ProviderFailure` enum, and the `ProviderFailures.Code` switch added by the multilingual work.
- Create: `tests/Meander.Tests/ConnectionFixtures.cs`
- Test: `tests/Meander.Tests/ConnectionServiceTests.cs`

**Interfaces:**
- Produces, all in namespace `Meander.Core`:

```csharp
public enum ServiceKind { Garmin }
public sealed record ServiceTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? ExternalUserId);
public sealed record ServiceConnection(Guid SessionId, ServiceKind Service, ServiceTokens Tokens, DateTimeOffset ConnectedAt);
public sealed record SentRoute(string ExternalId, Uri? ViewUrl);
public interface IConnectionStore { ServiceConnection? Get(Guid sessionId, ServiceKind service); IReadOnlyList<ServiceConnection> List(Guid sessionId);
    void Upsert(ServiceConnection connection); void Delete(Guid sessionId, ServiceKind service); int DeleteByExternalUser(ServiceKind service, string externalUserId); }
public interface IServiceConnector { ServiceKind Service { get; } bool IsConfigured { get; } Uri BuildAuthorizeUrl(string state, string codeChallenge, Uri redirectUri);
    Task<ServiceTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct);
    Task<ServiceTokens> RefreshAsync(ServiceTokens tokens, CancellationToken ct); Task RevokeAsync(ServiceTokens tokens, CancellationToken ct); }
public interface IRouteDestination { ServiceKind Service { get; } Task<SentRoute> SendAsync(RouteCandidate route, Activity activity, ServiceTokens tokens, CancellationToken ct); }
public sealed class ConnectionService(IConnectionStore store, IEnumerable<IServiceConnector> connectors, TimeProvider clock)
{
    IReadOnlyList<ServiceKind> Configured { get; }
    IServiceConnector? Connector(ServiceKind service);            // last registered + configured wins
    Task<ServiceTokens> TokensAsync(Guid sessionId, ServiceKind service, CancellationToken ct);
    Task<T> UseAsync<T>(Guid sessionId, ServiceKind service, Func<ServiceTokens, Task<T>> action, CancellationToken ct);
    ServiceConnection? Remove(Guid sessionId, ServiceKind service);
}
```

- `ProviderFailure.Unauthorized` means there is no connection, or the service rejected the tokens.
- The tests rely on these fakes in `ConnectionFixtures.cs`: `MemoryConnectionStore`, `FakeConnector`, `FakeDestination`.

- [ ] **Step 1: Write the fixtures** in `tests/Meander.Tests/ConnectionFixtures.cs`

```csharp
using Meander.Core;

namespace Meander.Tests;

internal sealed class MemoryConnectionStore : IConnectionStore
{
    public readonly Dictionary<(Guid, ServiceKind), ServiceConnection> Items = [];
    public ServiceConnection? Get(Guid sessionId, ServiceKind service) { lock (Items) return Items.GetValueOrDefault((sessionId, service)); }
    public IReadOnlyList<ServiceConnection> List(Guid sessionId) { lock (Items) return Items.Values.Where(c => c.SessionId == sessionId).ToArray(); }
    public void Upsert(ServiceConnection connection) { lock (Items) Items[(connection.SessionId, connection.Service)] = connection; }
    public void Delete(Guid sessionId, ServiceKind service) { lock (Items) Items.Remove((sessionId, service)); }
    public int DeleteByExternalUser(ServiceKind service, string externalUserId)
    {
        lock (Items)
        {
            var keys = Items.Where(i => i.Key.Item2 == service && i.Value.Tokens.ExternalUserId == externalUserId).Select(i => i.Key).ToArray();
            foreach (var key in keys) Items.Remove(key);
            return keys.Length;
        }
    }
}

internal sealed class FakeConnector : IServiceConnector
{
    public ServiceKind Service => ServiceKind.Garmin;
    public bool IsConfigured { get; set; } = true;
    public ServiceTokens Next { get; set; } = new("access-2", "refresh-2", DateTimeOffset.UtcNow.AddHours(1), null);
    public bool RejectRefresh { get; set; }
    public int Refreshes;
    public int Revokes;
    public readonly List<(string Code, string Verifier, Uri RedirectUri)> Exchanges = [];
    public Uri BuildAuthorizeUrl(string state, string codeChallenge, Uri redirectUri) =>
        new($"https://provider.test/authorize?state={Uri.EscapeDataString(state)}&code_challenge={codeChallenge}&redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}");
    public Task<ServiceTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct)
    {
        Exchanges.Add((code, codeVerifier, redirectUri));
        return code == "bad" ? throw new ProviderException(ProviderFailure.Unauthorized, "rejected") : Task.FromResult(Next with { ExternalUserId = "user-1" });
    }
    public async Task<ServiceTokens> RefreshAsync(ServiceTokens tokens, CancellationToken ct)
    {
        Interlocked.Increment(ref Refreshes);
        await Task.Delay(20, ct);
        return RejectRefresh ? throw new ProviderException(ProviderFailure.Unauthorized, "rejected") : Next;
    }
    public Task RevokeAsync(ServiceTokens tokens, CancellationToken ct) { Interlocked.Increment(ref Revokes); return Task.CompletedTask; }
}

internal sealed class FakeDestination : IRouteDestination
{
    public ServiceKind Service => ServiceKind.Garmin;
    public bool Reject { get; set; }
    public readonly List<(RouteCandidate Route, Activity Activity, ServiceTokens Tokens)> Sent = [];
    public Task<SentRoute> SendAsync(RouteCandidate route, Activity activity, ServiceTokens tokens, CancellationToken ct)
    {
        if (Reject) throw new ProviderException(ProviderFailure.Unauthorized, "token revoked");
        Sent.Add((route, activity, tokens));
        return Task.FromResult(new SentRoute("course-1", new Uri("https://provider.test/course/1")));
    }
}
```

- [ ] **Step 2: Write the failing tests** in `tests/Meander.Tests/ConnectionServiceTests.cs`

```csharp
using Meander.Core;

namespace Meander.Tests;

public sealed class ConnectionServiceTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private readonly TestClock clock = new();
    private readonly MemoryConnectionStore store = new();
    private readonly FakeConnector connector = new();
    private ConnectionService Service() => new(store, [connector], clock);
    private void Connect(TimeSpan expiresIn, string? refresh = "refresh-1") =>
        store.Upsert(new(Session, ServiceKind.Garmin, new("access-1", refresh, clock.Now + expiresIn, "user-1"), clock.Now));

    [Fact]
    public async Task ReturnsStoredTokensWhileTheyAreValid()
    {
        Connect(TimeSpan.FromHours(1));
        Assert.Equal("access-1", (await Service().TokensAsync(Session, ServiceKind.Garmin, default)).AccessToken);
        Assert.Equal(0, connector.Refreshes);
    }

    [Fact]
    public async Task RefreshesNearExpiryOnceAndKeepsTheUserId()
    {
        Connect(TimeSpan.FromMinutes(4));
        var service = Service();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => service.TokensAsync(Session, ServiceKind.Garmin, default)));
        Assert.All(results, t => Assert.Equal("access-2", t.AccessToken));
        Assert.Equal(1, connector.Refreshes);
        Assert.Equal("user-1", store.Get(Session, ServiceKind.Garmin)!.Tokens.ExternalUserId);
    }

    [Fact]
    public async Task RejectedRefreshRemovesTheConnection()
    {
        Connect(TimeSpan.FromMinutes(1));
        connector.RejectRefresh = true;
        var failure = await Assert.ThrowsAsync<ProviderException>(() => Service().TokensAsync(Session, ServiceKind.Garmin, default));
        Assert.Equal(ProviderFailure.Unauthorized, failure.Failure);
        Assert.Null(store.Get(Session, ServiceKind.Garmin));
    }

    [Fact]
    public async Task MissingOrUnconfiguredConnectionsAreUnauthorized()
    {
        Assert.Equal(ProviderFailure.Unauthorized, (await Assert.ThrowsAsync<ProviderException>(() => Service().TokensAsync(Session, ServiceKind.Garmin, default))).Failure);
        Connect(TimeSpan.FromHours(1));
        connector.IsConfigured = false;
        Assert.Empty(Service().Configured);
        Assert.Equal(ProviderFailure.Unauthorized, (await Assert.ThrowsAsync<ProviderException>(() => Service().TokensAsync(Session, ServiceKind.Garmin, default))).Failure);
    }

    [Fact]
    public async Task UseAsyncDropsTheConnectionWhenTheServiceRejectsIt()
    {
        Connect(TimeSpan.FromHours(1));
        var destination = new FakeDestination { Reject = true };
        await Assert.ThrowsAsync<ProviderException>(() => Service().UseAsync(Session, ServiceKind.Garmin,
            tokens => destination.SendAsync(Fixtures.Candidate(), Activity.Running, tokens, default), default));
        Assert.Null(store.Get(Session, ServiceKind.Garmin));
    }

    [Fact]
    public void RemoveReturnsTheDeletedConnection()
    {
        Connect(TimeSpan.FromHours(1));
        Assert.Equal("access-1", Service().Remove(Session, ServiceKind.Garmin)!.Tokens.AccessToken);
        Assert.Null(store.Get(Session, ServiceKind.Garmin));
        Assert.Null(Service().Remove(Session, ServiceKind.Garmin));
    }
}
```

- [ ] **Step 3: Run the tests and confirm they fail**

Run: `dotnet test Meander.slnx --filter ConnectionServiceTests`
Expected: the build fails because `ServiceKind` and the other new types do not exist.

- [ ] **Step 4: Implement**

`Domain.cs`:
- Change the enum to `public enum ProviderFailure { Unavailable, Throttled, Timeout, InvalidResponse, NoRoute, Unauthorized }`.
- In `ProviderFailures.Code`, add the arm `ProviderFailure.Unauthorized => "unauthorized",`.
- In `frontend/src/locales/en.ts`, add `'errors.unauthorized': 'The connection has expired. Connect again.',`.
- In `nl.ts`, add `'errors.unauthorized': 'De koppeling is verlopen. Koppel opnieuw.',`.

`src/Meander.Core/Connections.cs`:

```csharp
using System.Collections.Concurrent;

namespace Meander.Core;

// A connection (who you are at a service) is separate from what a service can do,
// so pulling data later (an activity source) reuses the same tokens and refresh logic.
public enum ServiceKind { Garmin }
public sealed record ServiceTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? ExternalUserId);
public sealed record ServiceConnection(Guid SessionId, ServiceKind Service, ServiceTokens Tokens, DateTimeOffset ConnectedAt);
public sealed record SentRoute(string ExternalId, Uri? ViewUrl);

public interface IConnectionStore
{
    ServiceConnection? Get(Guid sessionId, ServiceKind service);
    IReadOnlyList<ServiceConnection> List(Guid sessionId);
    void Upsert(ServiceConnection connection);
    void Delete(Guid sessionId, ServiceKind service);
    int DeleteByExternalUser(ServiceKind service, string externalUserId);
}

public interface IServiceConnector
{
    ServiceKind Service { get; }
    bool IsConfigured { get; }
    Uri BuildAuthorizeUrl(string state, string codeChallenge, Uri redirectUri);
    Task<ServiceTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct);
    // Throws ProviderException(Unauthorized) when the service rejects the refresh token.
    Task<ServiceTokens> RefreshAsync(ServiceTokens tokens, CancellationToken ct);
    Task RevokeAsync(ServiceTokens tokens, CancellationToken ct);
}

public interface IRouteDestination
{
    ServiceKind Service { get; }
    Task<SentRoute> SendAsync(RouteCandidate route, Activity activity, ServiceTokens tokens, CancellationToken ct);
}

public sealed class ConnectionService(IConnectionStore store, IEnumerable<IServiceConnector> connectors, TimeProvider clock)
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    // Services rotate refresh tokens, so two parallel refreshes would invalidate each other.
    private static readonly ConcurrentDictionary<(Guid, ServiceKind), SemaphoreSlim> RefreshLocks = new();

    public IReadOnlyList<ServiceKind> Configured => Enum.GetValues<ServiceKind>().Where(s => Connector(s) is not null).ToArray();
    public IServiceConnector? Connector(ServiceKind service) => connectors.LastOrDefault(c => c.Service == service && c.IsConfigured);

    public async Task<T> UseAsync<T>(Guid sessionId, ServiceKind service, Func<ServiceTokens, Task<T>> action, CancellationToken ct)
    {
        var tokens = await TokensAsync(sessionId, service, ct);
        try { return await action(tokens); }
        catch (ProviderException { Failure: ProviderFailure.Unauthorized }) { store.Delete(sessionId, service); throw; }
    }

    public async Task<ServiceTokens> TokensAsync(Guid sessionId, ServiceKind service, CancellationToken ct)
    {
        var connector = Connector(service) ?? throw NotConnected(service);
        var current = store.Get(sessionId, service) ?? throw NotConnected(service);
        if (Fresh(current.Tokens)) return current.Tokens;
        var gate = RefreshLocks.GetOrAdd((sessionId, service), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Another request may have refreshed while this one waited.
            current = store.Get(sessionId, service) ?? throw NotConnected(service);
            if (Fresh(current.Tokens)) return current.Tokens;
            if (current.Tokens.RefreshToken is null) { store.Delete(sessionId, service); throw NotConnected(service); }
            ServiceTokens refreshed;
            try { refreshed = await connector.RefreshAsync(current.Tokens, ct); }
            catch (ProviderException { Failure: ProviderFailure.Unauthorized }) { store.Delete(sessionId, service); throw; }
            refreshed = refreshed with
            {
                RefreshToken = refreshed.RefreshToken ?? current.Tokens.RefreshToken,
                ExternalUserId = refreshed.ExternalUserId ?? current.Tokens.ExternalUserId
            };
            store.Upsert(current with { Tokens = refreshed });
            return refreshed;
        }
        finally { gate.Release(); }
    }

    public ServiceConnection? Remove(Guid sessionId, ServiceKind service)
    {
        var connection = store.Get(sessionId, service);
        if (connection is not null) store.Delete(sessionId, service);
        return connection;
    }

    private bool Fresh(ServiceTokens tokens) => tokens.ExpiresAt - clock.GetUtcNow() > RefreshMargin;
    private static ProviderException NotConnected(ServiceKind service) => new(ProviderFailure.Unauthorized, $"Connect {service} first.");
}
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test Meander.slnx --filter ConnectionServiceTests`
Expected: 6 tests pass. Then run `dotnet test Meander.slnx`; all tests must pass, because the new enum value cannot break an existing `switch`.

- [ ] **Step 6: Commit**

```bash
git add src/Meander.Core tests/Meander.Tests frontend/src/locales
git commit -m "Add the service connection core with token refresh"
```

---

### Task 3: Encrypted connection store and PKCE

**Files:**
- Create: `src/Meander.Infrastructure/Connections/SqliteConnectionStore.cs`
- Create: `src/Meander.Infrastructure/Connections/OAuthPkce.cs`
- Test: `tests/Meander.Tests/ConnectionStoreTests.cs`

**Interfaces:**
- Consumes: `IConnectionStore`, `ServiceConnection` (Task 2), `ResultStorageOptions` (existing).
- Produces:
  - `public sealed class SqliteConnectionStore(ResultStorageOptions options, IDataProtectionProvider protection) : IConnectionStore, IDisposable`
  - `public sealed record PkcePair(string State, string Verifier, string Challenge)`
  - `public static class OAuthPkce { PkcePair Create(); string ChallengeFor(string verifier); }`

- [ ] **Step 1: Write the failing tests** in `tests/Meander.Tests/ConnectionStoreTests.cs`

```csharp
using Meander.Core;
using Meander.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Meander.Tests;

public sealed class ConnectionStoreTests
{
    private static ServiceConnection Connection(Guid session, string user = "garmin-user") =>
        new(session, ServiceKind.Garmin, new("secret-access", "secret-refresh", DateTimeOffset.UtcNow.AddHours(1), user), DateTimeOffset.UtcNow);

    [Fact]
    public void RoundTripsUpsertsAndDeletes()
    {
        using var store = new SqliteConnectionStore(new ResultStorageOptions { DatabasePath = ":memory:" }, new EphemeralDataProtectionProvider());
        var session = Guid.NewGuid();
        store.Upsert(Connection(session));
        Assert.Equal("secret-access", store.Get(session, ServiceKind.Garmin)!.Tokens.AccessToken);
        store.Upsert(Connection(session) with { Tokens = Connection(session).Tokens with { AccessToken = "newer" } });
        Assert.Equal("newer", Assert.Single(store.List(session)).Tokens.AccessToken);
        store.Delete(session, ServiceKind.Garmin);
        Assert.Null(store.Get(session, ServiceKind.Garmin));
    }

    [Fact]
    public void DeletesEveryConnectionOfAnExternalUser()
    {
        using var store = new SqliteConnectionStore(new ResultStorageOptions { DatabasePath = ":memory:" }, new EphemeralDataProtectionProvider());
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        store.Upsert(Connection(a)); store.Upsert(Connection(b)); store.Upsert(Connection(c, "someone-else"));
        Assert.Equal(2, store.DeleteByExternalUser(ServiceKind.Garmin, "garmin-user"));
        Assert.Null(store.Get(a, ServiceKind.Garmin));
        Assert.NotNull(store.Get(c, ServiceKind.Garmin));
    }

    [Fact]
    public void StoresNoPlainTokensOrUserIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meander-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (var store = new SqliteConnectionStore(new ResultStorageOptions { DatabasePath = path }, new EphemeralDataProtectionProvider()))
                store.Upsert(Connection(Guid.NewGuid()));
            using var raw = new SqliteConnection($"Data Source={path};Pooling=False");
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "SELECT external_user || json FROM connections";
            var stored = (string)command.ExecuteScalar()!;
            Assert.DoesNotContain("secret-", stored);
            Assert.DoesNotContain("garmin-user", stored);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public void UnreadableRowsCountAsDisconnected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meander-{Guid.NewGuid():N}.sqlite");
        try
        {
            var session = Guid.NewGuid();
            using (var store = new SqliteConnectionStore(new ResultStorageOptions { DatabasePath = path }, new EphemeralDataProtectionProvider()))
                store.Upsert(Connection(session));
            // Same database, different key ring: as after losing data/keys.
            using var other = new SqliteConnectionStore(new ResultStorageOptions { DatabasePath = path }, new EphemeralDataProtectionProvider());
            Assert.Null(other.Get(session, ServiceKind.Garmin));
            Assert.Empty(other.List(session));
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public void PkceMatchesTheRfcExampleAndIsRandom()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OAuthPkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var (first, second) = (OAuthPkce.Create(), OAuthPkce.Create());
        Assert.NotEqual(first.Verifier, second.Verifier);
        Assert.NotEqual(first.State, second.State);
        Assert.Equal(OAuthPkce.ChallengeFor(first.Verifier), first.Challenge);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.Verifier);
    }
}
```

The fourth test reopens the same database file with a different key ring. The rows it cannot decrypt must be skipped, not thrown.

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test Meander.slnx --filter ConnectionStoreTests`
Expected: the build fails because `SqliteConnectionStore` does not exist.

- [ ] **Step 3: Implement `OAuthPkce`**

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Meander.Infrastructure;

public sealed record PkcePair(string State, string Verifier, string Challenge);

// RFC 7636 PKCE with the S256 method.
public static class OAuthPkce
{
    public static PkcePair Create()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return new(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)), verifier, ChallengeFor(verifier));
    }
    public static string ChallengeFor(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
```

- [ ] **Step 4: Implement `SqliteConnectionStore`**

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meander.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Meander.Infrastructure;

// Tokens are encrypted with Data Protection; the external user id is only kept as a hash so
// deregistration webhooks can find it. Unlike saved searches, connections change and disappear.
public sealed class SqliteConnectionStore : IConnectionStore, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly SqliteConnection connection;
    private readonly IDataProtector protector;
    private readonly object gate = new();

    public SqliteConnectionStore(ResultStorageOptions options, IDataProtectionProvider protection)
    {
        protector = protection.CreateProtector("Meander.Connections.v1");
        if (string.IsNullOrWhiteSpace(options.DatabasePath)) throw new InvalidOperationException("ResultStorage:DatabasePath is required.");
        if (options.DatabasePath != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS connections (
                session_id TEXT NOT NULL, service TEXT NOT NULL, external_user TEXT, json TEXT NOT NULL,
                PRIMARY KEY (session_id, service));
            CREATE INDEX IF NOT EXISTS connections_external_user ON connections (service, external_user);
            """;
        command.ExecuteNonQuery();
    }

    public ServiceConnection? Get(Guid sessionId, ServiceKind service) =>
        Query("SELECT json FROM connections WHERE session_id = $session AND service = $service",
            ("$session", sessionId.ToString()), ("$service", service.ToString())).FirstOrDefault();

    public IReadOnlyList<ServiceConnection> List(Guid sessionId) =>
        Query("SELECT json FROM connections WHERE session_id = $session", ("$session", sessionId.ToString()));

    public void Upsert(ServiceConnection value) => Execute("""
        INSERT INTO connections (session_id, service, external_user, json) VALUES ($session, $service, $user, $json)
        ON CONFLICT(session_id, service) DO UPDATE SET external_user = excluded.external_user, json = excluded.json
        """, ("$session", value.SessionId.ToString()), ("$service", value.Service.ToString()),
        ("$user", Hash(value.Tokens.ExternalUserId)), ("$json", protector.Protect(JsonSerializer.Serialize(value, Json))));

    public void Delete(Guid sessionId, ServiceKind service) =>
        Execute("DELETE FROM connections WHERE session_id = $session AND service = $service",
            ("$session", sessionId.ToString()), ("$service", service.ToString()));

    public int DeleteByExternalUser(ServiceKind service, string externalUserId) =>
        Execute("DELETE FROM connections WHERE service = $service AND external_user = $user",
            ("$service", service.ToString()), ("$user", Hash(externalUserId)));

    private List<ServiceConnection> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (gate)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            var results = new List<ServiceConnection>();
            while (reader.Read())
            {
                // Rows from lost or rotated-out keys cannot be read: treat them as disconnected.
                try { results.Add(JsonSerializer.Deserialize<ServiceConnection>(protector.Unprotect(reader.GetString(0)), Json)!); }
                catch (CryptographicException) { }
            }
            return results;
        }
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (gate)
        {
            using var command = Command(sql, parameters);
            return command.ExecuteNonQuery();
        }
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string? Hash(string? value) => value is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public void Dispose() { lock (gate) connection.Dispose(); }
}
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test Meander.slnx --filter ConnectionStoreTests`
Expected: 5 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Meander.Infrastructure/Connections tests/Meander.Tests/ConnectionStoreTests.cs
git commit -m "Store service connections encrypted and add PKCE"
```

---

### Task 4: Connection endpoints and send-route API

**Files:**
- Create: `src/Meander.Api/ConnectionEndpoints.cs`
- Modify: `src/Meander.Api/Program.cs`
- Modify: `src/Meander.Api/appsettings.json`
- Modify: `tests/Meander.Tests/ApiTests.cs` (`ApiFactory`)
- Test: `tests/Meander.Tests/ConnectionApiTests.cs`

**Interfaces:**
- Consumes: `ConnectionService`, `IConnectionStore`, `IRouteDestination`, `OAuthPkce`, `SqliteConnectionStore`, `RouteLookup`.
- Produces the HTTP API:
  - `GET /api/connections` returns `[{ "service": "garmin", "connected": bool }]`.
  - `GET /api/connections/{service}/authorize?returnUrl=` returns 302. It returns 404 with code `serviceNotFound` for an unknown or unconfigured service.
  - `GET /api/connections/{service}/callback?code=&state=&error=`:
    - success: 302 to `returnUrl` with `connected={service}` added
    - provider error or failed exchange: 302 to `returnUrl` with `connectError={service}` added
    - bad state: 400 with code `connectionStateInvalid`
  - `DELETE /api/connections/{service}` returns 204.
  - `POST /api/routes/{id}/send/{service}`:
    - success: 200 `{ externalId, viewUrl }`
    - no session cookie: 401 with code `notConnected`
    - expired or rejected: 401 with code `connectionExpired`
    - unknown route: 404 with code `routeNotFound`
    - service failure: 502 with code `serviceUnavailable`
- Produces: `public sealed class ConnectionOptions { public string PublicBaseUrl { get; set; } }`, bound from the root key `PublicBaseUrl`.
- `ApiFactory` gains an optional fourth constructor parameter, `Action<IServiceCollection>? configure`. It runs last in `ConfigureServices`.

- [ ] **Step 1: Extend `ApiFactory`** in `ApiTests.cs`

Change the class declaration and `ConfigureServices`. Add the two settings to the base dictionary:

```csharp
internal sealed class ApiFactory(StubOrs handler, Dictionary<string, string?>? settings = null, IWoodlandProvider? woodlands = null,
    Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
```

In the base settings dictionary, add these two entries:

```csharp
["PublicBaseUrl"] = "http://localhost",
["DataProtection:KeysPath"] = Path.Combine(Path.GetTempPath(), "meander-test-keys"),
```

At the end of the `ConfigureServices` lambda, add:

```csharp
configure?.Invoke(services);
```

- [ ] **Step 2: Write the failing tests** in `tests/Meander.Tests/ConnectionApiTests.cs`

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meander.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Meander.Tests;

public sealed class ConnectionApiTests
{
    private readonly FakeConnector connector = new();
    private readonly FakeDestination destination = new();
    private ApiFactory App(Dictionary<string, string?>? settings = null) => new(new(), settings, configure: services =>
    {
        services.AddSingleton<IServiceConnector>(connector);
        services.AddSingleton<IRouteDestination>(destination);
    });
    private static HttpClient Client(ApiFactory app) => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    private static async Task<string> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    private static async Task<string> Authorize(HttpClient client, string returnUrl = "/routes/s?route=r")
    {
        using var response = await client.GetAsync($"/api/connections/garmin/authorize?returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("provider.test", location.Host);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("http://localhost/api/connections/garmin/callback", query["redirect_uri"].ToString());
        return query["state"].ToString();
    }

    private static async Task<string> RouteId(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/routes/generate",
            new { start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 5, activity = "walking" });
        return (await response.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task ConnectsSendsAndDisconnects()
    {
        await using var app = App();
        using var client = Client(app);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());

        var state = await Authorize(client);
        using var callback = await client.GetAsync($"/api/connections/garmin/callback?code=abc&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/routes/s?route=r&connected=garmin", callback.Headers.Location!.OriginalString);
        var exchange = Assert.Single(connector.Exchanges);
        Assert.Equal("abc", exchange.Code);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", exchange.Verifier);

        var status = (await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0];
        Assert.Equal("garmin", status.GetProperty("service").GetString());
        Assert.True(status.GetProperty("connected").GetBoolean());

        var id = await RouteId(client);
        using var sent = await client.PostAsync($"/api/routes/{id}/send/garmin", null);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("course-1", (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("externalId").GetString());
        var delivered = Assert.Single(destination.Sent);
        Assert.Equal(Activity.Walking, delivered.Activity);
        Assert.Equal(id, delivered.Route.Id.ToString());

        using var removed = await client.DeleteAsync("/api/connections/garmin");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(1, connector.Revokes);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task RejectsAForeignOrReplayedState()
    {
        await using var app = App();
        using var client = Client(app);
        await Authorize(client);
        using var forged = await client.GetAsync("/api/connections/garmin/callback?code=abc&state=forged");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal("connectionStateInvalid", await Code(forged));
        Assert.Empty(connector.Exchanges);

        var state = await Authorize(client);
        (await client.GetAsync($"/api/connections/garmin/callback?code=abc&state={state}")).Dispose();
        using var replay = await client.GetAsync($"/api/connections/garmin/callback?code=abc&state={state}");
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.test/x", "/")]
    [InlineData("//evil.test/x", "/")]
    [InlineData("/\\evil.test", "/")]
    [InlineData("/routes/a?route=b", "/routes/a?route=b")]
    public async Task OnlyReturnsToLocalPaths(string returnUrl, string expected)
    {
        await using var app = App();
        using var client = Client(app);
        var state = await Authorize(client, returnUrl);
        using var callback = await client.GetAsync($"/api/connections/garmin/callback?code=abc&state={state}");
        Assert.StartsWith(expected, callback.Headers.Location!.OriginalString);
        Assert.Contains("connected=garmin", callback.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task DeniedOrFailedConnectionsReturnToTheApp()
    {
        await using var app = App();
        using var client = Client(app);
        var denied = await Authorize(client);
        using var deny = await client.GetAsync($"/api/connections/garmin/callback?error=access_denied&state={denied}");
        Assert.Equal("/routes/s?route=r&connectError=garmin", deny.Headers.Location!.OriginalString);
        var failed = await Authorize(client);
        using var fail = await client.GetAsync($"/api/connections/garmin/callback?code=bad&state={failed}");
        Assert.Equal("/routes/s?route=r&connectError=garmin", fail.Headers.Location!.OriginalString);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task SendingNeedsALiveConnection()
    {
        await using var app = App();
        using var client = Client(app);
        var id = await RouteId(client);
        using var anonymous = await client.PostAsync($"/api/routes/{id}/send/garmin", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("notConnected", await Code(anonymous));

        var state = await Authorize(client);
        (await client.GetAsync($"/api/connections/garmin/callback?code=abc&state={state}")).Dispose();
        destination.Reject = true;
        using var revoked = await client.PostAsync($"/api/routes/{id}/send/garmin", null);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("connectionExpired", await Code(revoked));
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());

        using var missing = await client.PostAsync($"/api/routes/{Guid.NewGuid()}/send/garmin", null);
        Assert.Equal("routeNotFound", await Code(missing));
    }

    [Fact]
    public async Task UnknownOrUnconfiguredServicesAreNotFound()
    {
        connector.IsConfigured = false;
        await using var app = App();
        using var client = Client(app);
        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/connections") ?? []);
        using var garmin = await client.GetAsync("/api/connections/garmin/authorize");
        Assert.Equal("serviceNotFound", await Code(garmin));
        using var unknown = await client.GetAsync("/api/connections/strava/authorize");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AuthorizeMovesOtherHostsToThePublicHostFirst()
    {
        await using var app = App(new() { ["AllowedHosts"] = "*", ["PublicBaseUrl"] = "https://meander.roesink.dev" });
        using var client = Client(app);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/connections/garmin/authorize?returnUrl=%2Froutes%2Fa");
        request.Headers.Host = "meander.sander.ninja";
        using var response = await client.SendAsync(request);
        Assert.Equal("https://meander.roesink.dev/api/connections/garmin/authorize?returnUrl=%2Froutes%2Fa", response.Headers.Location!.ToString());
    }
}
```

- [ ] **Step 3: Run the tests and confirm they fail**

Run: `dotnet test Meander.slnx --filter ConnectionApiTests`
Expected: they fail with 404 on `/api/connections`.

- [ ] **Step 4: Implement `ConnectionEndpoints.cs`**

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meander.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Meander.Infrastructure;

public sealed class ConnectionOptions
{
    public string PublicBaseUrl { get; set; } = "";
}

// OAuth for every service, tied to an anonymous browser session. Capabilities (sending a
// route now, reading data later) only receive tokens through ConnectionService.
public static class ConnectionEndpoints
{
    public const string SessionCookie = "meander_sid";
    public const string PendingCookie = "meander_oauth";
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(365);

    private sealed record PendingAuthorization(string State, string Verifier, ServiceKind Service, string ReturnUrl, DateTimeOffset ExpiresAt);

    public static void MapConnectionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/connections", (HttpContext context, ConnectionService connections, IConnectionStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var connected = SessionId(context) is { } session ? store.List(session).Select(c => c.Service).ToHashSet() : [];
            return Results.Ok(connections.Configured.Select(service => new { service, connected = connected.Contains(service) }));
        });

        app.MapGet("/api/connections/{service}/authorize", (string service, string? returnUrl, HttpContext context,
            ConnectionService connections, ConnectionOptions options, IDataProtectionProvider protection, TimeProvider clock) =>
        {
            if (Connector(service, connections) is not { } connector) return ServiceNotFound();
            // Cookies are per host, so the flow must start on the host the callback returns to.
            var publicBase = new Uri(options.PublicBaseUrl);
            if (!string.Equals(context.Request.Host.Host, publicBase.Host, StringComparison.OrdinalIgnoreCase))
                return Results.Redirect(new Uri(publicBase, context.Request.Path + context.Request.QueryString).ToString());
            if (SessionId(context) is null)
                context.Response.Cookies.Append(SessionCookie, Guid.NewGuid().ToString(), Cookie(options, SessionLifetime));
            var pkce = OAuthPkce.Create();
            var pending = new PendingAuthorization(pkce.State, pkce.Verifier, connector.Service, LocalPath(returnUrl), clock.GetUtcNow() + PendingLifetime);
            context.Response.Cookies.Append(PendingCookie, Pending(protection).Protect(JsonSerializer.Serialize(pending)), Cookie(options, PendingLifetime));
            return Results.Redirect(connector.BuildAuthorizeUrl(pkce.State, pkce.Challenge, RedirectUri(options, connector.Service)).ToString());
        }).RequireRateLimiting("connect");

        app.MapGet("/api/connections/{service}/callback", async (string service, string? code, string? state, string? error,
            HttpContext context, ConnectionService connections, IConnectionStore store, ConnectionOptions options,
            IDataProtectionProvider protection, TimeProvider clock, ILogger<ConnectionService> logger, CancellationToken ct) =>
        {
            var pending = ReadPending(context, protection);
            context.Response.Cookies.Delete(PendingCookie, Cookie(options, TimeSpan.Zero));
            if (Connector(service, connections) is not { } connector) return ServiceNotFound();
            if (pending is null || SessionId(context) is not { } session || pending.Service != connector.Service ||
                pending.ExpiresAt < clock.GetUtcNow() || state is null ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(pending.State)))
                return Results.Problem(statusCode: 400, title: "Connection could not be completed",
                    detail: "Start connecting again from Meander.", extensions: Code("connectionStateInvalid"));
            var failed = QueryHelpers.AddQueryString(pending.ReturnUrl, "connectError", Name(connector.Service));
            if (error is not null || string.IsNullOrEmpty(code)) return Results.Redirect(failed);
            try
            {
                var tokens = await connector.ExchangeCodeAsync(code, pending.Verifier, RedirectUri(options, connector.Service), ct);
                store.Upsert(new ServiceConnection(session, connector.Service, tokens, clock.GetUtcNow()));
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException ||
                                       ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Connecting {Service} failed: {Failure}", connector.Service, ex.GetType().Name);
                return Results.Redirect(failed);
            }
            return Results.Redirect(QueryHelpers.AddQueryString(pending.ReturnUrl, "connected", Name(connector.Service)));
        }).RequireRateLimiting("connect");

        app.MapDelete("/api/connections/{service}", async (string service, HttpContext context, ConnectionService connections,
            ILogger<ConnectionService> logger, CancellationToken ct) =>
        {
            if (Connector(service, connections) is not { } connector) return ServiceNotFound();
            if (SessionId(context) is { } session && connections.Remove(session, connector.Service) is { } removed)
            {
                try { await connector.RevokeAsync(removed.Tokens, ct); }
                catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException)
                { logger.LogWarning("Revoking {Service} failed: {Failure}", connector.Service, ex.GetType().Name); }
            }
            return Results.NoContent();
        }).RequireRateLimiting("connect");

        app.MapPost("/api/routes/{id:guid}/send/{service}", async (Guid id, string service, HttpContext context,
            ConnectionService connections, IEnumerable<IRouteDestination> destinations, IRouteSearchStore searches,
            ICandidateStore cache, ILogger<ConnectionService> logger, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (Connector(service, connections) is not { } connector ||
                destinations.LastOrDefault(d => d.Service == connector.Service) is not { } destination) return ServiceNotFound();
            if (RouteLookup.Find(id, searches, cache) is not { } found)
                return Results.Problem(statusCode: 404, title: "Route not found",
                    detail: "This route is unavailable. Open a saved route or generate a new one.", extensions: Code("routeNotFound"));
            if (SessionId(context) is not { } session)
                return Results.Problem(statusCode: 401, title: "Not connected", detail: $"Connect {connector.Service} first.", extensions: Code("notConnected"));
            try
            {
                return Results.Ok(await connections.UseAsync(session, connector.Service,
                    tokens => destination.SendAsync(found.Route, found.Activity, tokens, ct), ct));
            }
            catch (ProviderException { Failure: ProviderFailure.Unauthorized })
            {
                return Results.Problem(statusCode: 401, title: "Connection expired",
                    detail: $"Connect {connector.Service} again to send this route.", extensions: Code("connectionExpired"));
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException ||
                                       ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Sending a route to {Service} failed: {Failure}", connector.Service, ex.Message);
                return Results.Problem(statusCode: 502, title: "Service unavailable",
                    detail: $"{connector.Service} did not accept the route. Try again shortly.", extensions: Code("serviceUnavailable"));
            }
        }).RequireRateLimiting("connect");
    }

    public static Guid? SessionId(HttpContext context) =>
        Guid.TryParse(context.Request.Cookies[SessionCookie], out var id) ? id : null;

    public static string LocalPath(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl[0] == '/' && (returnUrl.Length == 1 || returnUrl[1] is not ('/' or '\\')) ? returnUrl : "/";

    private static IServiceConnector? Connector(string service, ConnectionService connections) =>
        Enum.TryParse<ServiceKind>(service, ignoreCase: true, out var kind) && Enum.IsDefined(kind) ? connections.Connector(kind) : null;

    private static string Name(ServiceKind service) => service.ToString().ToLowerInvariant();

    private static Uri RedirectUri(ConnectionOptions options, ServiceKind service) =>
        new(new Uri(options.PublicBaseUrl), $"/api/connections/{Name(service)}/callback");

    private static IDataProtector Pending(IDataProtectionProvider protection) => protection.CreateProtector("Meander.OAuthPending.v1");

    private static PendingAuthorization? ReadPending(HttpContext context, IDataProtectionProvider protection)
    {
        if (context.Request.Cookies[PendingCookie] is not { } value) return null;
        try { return JsonSerializer.Deserialize<PendingAuthorization>(Pending(protection).Unprotect(value)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return null; }
    }

    private static CookieOptions Cookie(ConnectionOptions options, TimeSpan lifetime) => new()
    {
        HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = lifetime, IsEssential = true,
        Secure = options.PublicBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
    };

    private static IResult ServiceNotFound() => Results.Problem(statusCode: 404, title: "Service not available",
        detail: "This service is not available on this Meander.", extensions: Code("serviceNotFound"));

    private static Dictionary<string, object?> Code(string code) => new() { ["code"] = code };
}
```

- [ ] **Step 5: Wire it up in `Program.cs`**

1. Add `using Microsoft.AspNetCore.DataProtection;`, `using Microsoft.AspNetCore.DataProtection.KeyManagement;`, `using Microsoft.AspNetCore.DataProtection.Repositories;` and `using Microsoft.Extensions.Options;` to the usings.

2. Next to the other option instances (after `var mapData = new MapDataOptions();`), add:

```csharp
var connectionOptions = new ConnectionOptions();
builder.Services.AddSingleton(connectionOptions);
```

3. After `builder.Services.AddSingleton<IRouteSearchStore, SqliteRouteSearchStore>();`, add:

```csharp
builder.Services.AddSingleton<IConnectionStore, SqliteConnectionStore>();
builder.Services.AddTransient<ConnectionService>();
builder.Services.AddDataProtection().SetApplicationName("Meander");
// Resolved lazily so test hosts can point the key ring elsewhere.
builder.Services.AddOptions<KeyManagementOptions>().Configure<IConfiguration, IHostEnvironment>((o, config, env) =>
{
    if (config["DataProtection:KeysPath"] is { Length: > 0 } keys)
        o.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(Path.GetFullPath(keys, env.ContentRootPath)), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
});
```

4. In `AddRateLimiter`, after the `"preview"` policy, add:

```csharp
o.AddPolicy("connect", context => RateLimitPartition.GetFixedWindowLimiter(ClientKey(context),
    _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.ConnectPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
```

5. In `ApiRateOptions`, add `public int ConnectPerMinute { get; set; } = 20;`. Add `|| limits.ConnectPerMinute < 1` to the RateLimiting validation.

6. After the `Bind` calls, add the binding below. After `app.Services.GetRequiredService<IRouteSearchStore>();`, add `app.Services.GetRequiredService<IConnectionStore>();`.

```csharp
connectionOptions.PublicBaseUrl = builder.Configuration["PublicBaseUrl"] ?? "";
if (app.Services.GetRequiredService<ConnectionService>().Configured.Count > 0 &&
    (!Uri.TryCreate(connectionOptions.PublicBaseUrl, UriKind.Absolute, out var publicBase) || publicBase.Scheme is not ("http" or "https")))
    throw new InvalidOperationException("Set PublicBaseUrl (for example https://meander.roesink.dev) when a service integration is configured.");
```

7. Before the `app.MapFallback("/api/{**path}", …)` line, add `app.MapConnectionEndpoints();`.

8. In `appsettings.json`, add the root entries `"PublicBaseUrl": "",` and `"DataProtection": { "KeysPath": "data/keys" },`. Also add `"ConnectPerMinute": 20` inside `RateLimiting`.

9. In `frontend/src/locales/en.ts` and `nl.ts`, add these problem keys:

| key | en | nl |
|---|---|---|
| `errors.serviceNotFound` | `This service is not available.` | `Deze dienst is niet beschikbaar.` |
| `errors.connectionStateInvalid` | `The connection could not be completed. Please try again.` | `De koppeling kon niet worden voltooid. Probeer het opnieuw.` |
| `errors.notConnected` | `Connect the service first.` | `Koppel de dienst eerst.` |
| `errors.connectionExpired` | `The connection has expired. Connect again to send this route.` | `De koppeling is verlopen. Koppel opnieuw om deze route te versturen.` |
| `errors.serviceUnavailable` | `The service did not accept the route. Try again shortly.` | `De dienst nam de route niet aan. Probeer het zo opnieuw.` |

- [ ] **Step 6: Run the tests and confirm they pass**

Run: `dotnet test Meander.slnx`
Expected: every test passes, including the 9 cases in `ConnectionApiTests`. Then run `cd frontend && npx tsc -b`; it must pass.

- [ ] **Step 7: Commit**

```bash
git add src tests frontend/src/locales
git commit -m "Add OAuth connection endpoints and sending routes to a service"
```

---

### Task 5: Garmin connector, Courses API and deregistration

**Files:**
- Create: `src/Meander.Infrastructure/Connections/GarminOptions.cs`
- Create: `src/Meander.Infrastructure/Connections/GarminConnector.cs`
- Create: `src/Meander.Infrastructure/Connections/GarminCourseDestination.cs`
- Modify: `src/Meander.Api/Program.cs`, `src/Meander.Api/ConnectionEndpoints.cs` (webhook), `src/Meander.Api/appsettings.json`
- Test: `tests/Meander.Tests/GarminTests.cs`

**Interfaces:**
- Consumes: `IServiceConnector`, `IRouteDestination`, `ServiceTokens`, `ProviderException`.
- Produces:
  - `GarminOptions` with the string properties `ClientId`, `ClientSecret`, `AuthorizeUrl`, `TokenUrl`, `UserIdUrl`, `RegistrationUrl`, `CourseUrl`, plus `bool IsConfigured` and `void Validate()`. Bound from `Integrations:Garmin`.
  - `GarminConnector(HttpClient, GarminOptions, TimeProvider) : IServiceConnector`
  - `GarminCourseDestination(HttpClient, GarminOptions) : IRouteDestination`
  - `GarminCourseMapper.Map(RouteCandidate, Activity) : GarminCourse`
  - `POST /api/webhooks/garmin/deregistration` with body `{ "deregistrations": [{ "userId": "…" }] }` returns 200.

The Garmin payload and URLs below follow the Courses API and the OAuth2 PKCE spec as currently understood. All URLs are configurable. Once developer access is granted, check `GarminCourseMapper` and the URL defaults against Garmin's docs; only these files and `appsettings.json` should need to change.

- [ ] **Step 1: Write the failing tests** in `tests/Meander.Tests/GarminTests.cs`

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meander.Core;
using Meander.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Meander.Tests;

internal sealed class StubGarmin : HttpMessageHandler
{
    public readonly ConcurrentQueue<(HttpMethod Method, string Url, string? Authorization, string Body)> Requests = new();
    public HttpStatusCode CourseStatus { get; set; } = HttpStatusCode.OK;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((request.Method, request.RequestUri!.GetLeftPart(UriPartial.Path), request.Headers.Authorization?.ToString(), body));
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/oauth/token"))
        {
            var form = QueryHelpers.ParseQuery(body);
            if (form["grant_type"] == "refresh_token" && form["refresh_token"] == "dead")
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant" }) };
            return Json(new { access_token = $"garmin-access-{form["grant_type"]}", refresh_token = "garmin-refresh", expires_in = 86400, token_type = "bearer" });
        }
        if (path.EndsWith("/user/id")) return Json(new { userId = "garmin-user-1" });
        if (path.EndsWith("/user/registration")) return new(HttpStatusCode.NoContent);
        if (path.EndsWith("/course")) return CourseStatus == HttpStatusCode.OK ? Json(new { courseId = 987654 }) : new(CourseStatus);
        return new(HttpStatusCode.NotFound);
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}

public sealed class GarminTests
{
    private readonly StubGarmin garmin = new();
    private ApiFactory App() => new(new(), new() { ["Integrations:Garmin:ClientId"] = "client-1", ["Integrations:Garmin:ClientSecret"] = "secret-1" },
        configure: services =>
        {
            services.AddHttpClient<GarminConnector>().ConfigurePrimaryHttpMessageHandler(() => garmin);
            services.AddHttpClient<GarminCourseDestination>().ConfigurePrimaryHttpMessageHandler(() => garmin);
        });

    private static async Task Connect(HttpClient client)
    {
        using var authorize = await client.GetAsync("/api/connections/garmin/authorize?returnUrl=%2F");
        var location = authorize.Headers.Location!;
        Assert.Equal("https://connect.garmin.com/oauth2Confirm", location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("client-1", query["client_id"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        using var callback = await client.GetAsync($"/api/connections/garmin/callback?code=garmin-code&state={query["state"]}");
        Assert.Equal("/?connected=garmin", callback.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ConnectsWithPkceAndSendsACourse()
    {
        await using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Connect(client);
        var token = garmin.Requests.Single(r => r.Url.EndsWith("/oauth/token"));
        var form = QueryHelpers.ParseQuery(token.Body);
        Assert.Equal("authorization_code", form["grant_type"].ToString());
        Assert.Equal("garmin-code", form["code"].ToString());
        Assert.Equal("secret-1", form["client_secret"].ToString());
        Assert.Matches("^[A-Za-z0-9_-]{43}$", form["code_verifier"].ToString());
        Assert.Equal("http://localhost/api/connections/garmin/callback", form["redirect_uri"].ToString());
        Assert.Equal("Bearer garmin-access-authorization_code", garmin.Requests.Single(r => r.Url.EndsWith("/user/id")).Authorization);

        using var generated = await client.PostAsJsonAsync("/api/routes/generate",
            new { start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 5, activity = "running" });
        var first = (await generated.Content.ReadFromJsonAsync<JsonElement>())[0];
        var id = first.GetProperty("id").GetString();
        using var sent = await client.PostAsync($"/api/routes/{id}/send/garmin", null);
        var result = await sent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("987654", result.GetProperty("externalId").GetString());
        Assert.Equal("https://connect.garmin.com/modern/course/987654", result.GetProperty("viewUrl").GetString());
        var course = garmin.Requests.Single(r => r.Url.EndsWith("/course"));
        Assert.Equal("Bearer garmin-access-authorization_code", course.Authorization);
        var payload = JsonDocument.Parse(course.Body).RootElement;
        Assert.Equal("RUNNING", payload.GetProperty("activityType").GetString());
        Assert.Equal(Math.Round(first.GetProperty("distanceKm").GetDouble() * 1000, 1), payload.GetProperty("distance").GetDouble());
        Assert.True(payload.GetProperty("geoPoints").GetArrayLength() > 2);
    }

    [Fact]
    public async Task RejectedCourseTokensAskForAReconnect()
    {
        garmin.CourseStatus = HttpStatusCode.Unauthorized;
        await using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Connect(client);
        using var generated = await client.PostAsJsonAsync("/api/routes/generate",
            new { start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 5 });
        var id = (await generated.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetString();
        using var sent = await client.PostAsync($"/api/routes/{id}/send/garmin", null);
        Assert.Equal(HttpStatusCode.Unauthorized, sent.StatusCode);
    }

    [Fact]
    public async Task DeregistrationRemovesTheConnection()
    {
        await using var app = App();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Connect(client);
        using var hook = await client.PostAsJsonAsync("/api/webhooks/garmin/deregistration",
            new { deregistrations = new[] { new { userId = "garmin-user-1" } } });
        Assert.Equal(HttpStatusCode.OK, hook.StatusCode);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/connections"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task RefreshSendsTheRefreshTokenAndMapsInvalidGrant()
    {
        var options = new GarminOptions { ClientId = "client-1", ClientSecret = "secret-1" };
        var connector = new GarminConnector(new HttpClient(garmin), options, TimeProvider.System);
        var refreshed = await connector.RefreshAsync(new("old", "live", DateTimeOffset.UtcNow, "u"), default);
        Assert.Equal("garmin-access-refresh_token", refreshed.AccessToken);
        Assert.Equal("u", refreshed.ExternalUserId);
        var failure = await Assert.ThrowsAsync<ProviderException>(() => connector.RefreshAsync(new("old", "dead", DateTimeOffset.UtcNow, "u"), default));
        Assert.Equal(ProviderFailure.Unauthorized, failure.Failure);
    }

    [Fact]
    public void MapsRoutesToGarminCourses()
    {
        var course = GarminCourseMapper.Map(Fixtures.Candidate(), Activity.Walking);
        Assert.Equal("Meander 2.0 km", course.CourseName);
        Assert.Equal("WALKING", course.ActivityType);
        Assert.Equal("WGS84", course.CoordinateSystem);
        Assert.Equal(2000, course.Distance);
        Assert.Equal(new GarminGeoPoint(52.09, 5.12, 12.5), course.GeoPoints[0]);
        Assert.Null(course.GeoPoints[1].Elevation);
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("client", "", false)]
    [InlineData("client", "secret", true)]
    public void ValidatesCredentials(string id, string secret, bool valid)
    {
        var options = new GarminOptions { ClientId = id, ClientSecret = secret };
        if (valid) options.Validate(); else Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `dotnet test Meander.slnx --filter GarminTests`
Expected: the build fails because `GarminConnector` and the other Garmin types do not exist.

- [ ] **Step 3: Implement `GarminOptions.cs`**

```csharp
namespace Meander.Infrastructure;

// Garmin Connect Developer Program (OAuth 2.0 PKCE + Courses API). Every URL is configurable
// so a change on Garmin's side is a configuration change. Empty ClientId = integration off.
public sealed class GarminOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AuthorizeUrl { get; set; } = "https://connect.garmin.com/oauth2Confirm";
    public string TokenUrl { get; set; } = "https://connectapi.garmin.com/di-oauth2-service/oauth/token";
    public string UserIdUrl { get; set; } = "https://apis.garmin.com/wellness-api/rest/user/id";
    public string RegistrationUrl { get; set; } = "https://apis.garmin.com/wellness-api/rest/user/registration";
    public string CourseUrl { get; set; } = "https://apis.garmin.com/training-api/courses/v1/course";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public void Validate()
    {
        if (!IsConfigured) return;
        if (string.IsNullOrWhiteSpace(ClientSecret))
            throw new InvalidOperationException("Set Integrations__Garmin__ClientSecret together with the client id.");
        foreach (var url in new[] { AuthorizeUrl, TokenUrl, UserIdUrl, RegistrationUrl, CourseUrl })
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException($"Integrations:Garmin URL '{url}' must be an absolute HTTP or HTTPS URL.");
    }
}

public sealed record GarminDeregistrations(IReadOnlyList<GarminDeregistration>? Deregistrations);
public sealed record GarminDeregistration(string? UserId);
```

- [ ] **Step 4: Implement `GarminConnector.cs`**

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meander.Core;
using Microsoft.AspNetCore.WebUtilities;

namespace Meander.Infrastructure;

public sealed class GarminConnector(HttpClient http, GarminOptions options, TimeProvider clock) : IServiceConnector
{
    public ServiceKind Service => ServiceKind.Garmin;
    public bool IsConfigured => options.IsConfigured;

    public Uri BuildAuthorizeUrl(string state, string codeChallenge, Uri redirectUri) => new(QueryHelpers.AddQueryString(options.AuthorizeUrl,
        new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = options.ClientId, ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256", ["redirect_uri"] = redirectUri.ToString(), ["state"] = state
        }));

    public async Task<ServiceTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct)
    {
        var tokens = await TokenAsync(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = codeVerifier, ["redirect_uri"] = redirectUri.ToString()
        }, null, ct);
        return tokens with { ExternalUserId = await UserIdAsync(tokens.AccessToken, ct) };
    }

    public Task<ServiceTokens> RefreshAsync(ServiceTokens tokens, CancellationToken ct) => tokens.RefreshToken is { } refresh
        ? TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }, tokens.ExternalUserId, ct)
        : throw new ProviderException(ProviderFailure.Unauthorized, "Garmin connection has no refresh token.");

    public async Task RevokeAsync(ServiceTokens tokens, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, options.RegistrationUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Unauthorized)
            throw new ProviderException(ProviderFailure.Unavailable, $"Garmin revoke failed ({(int)response.StatusCode}).");
    }

    private async Task<ServiceTokens> TokenAsync(Dictionary<string, string> form, string? userId, CancellationToken ct)
    {
        form["client_id"] = options.ClientId;
        form["client_secret"] = options.ClientSecret;
        using var response = await http.PostAsync(options.TokenUrl, new FormUrlEncodedContent(form), ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            throw new ProviderException(ProviderFailure.Unauthorized, "Garmin rejected the authorization.");
        if (!response.IsSuccessStatusCode)
            throw new ProviderException(ProviderFailure.Unavailable, $"Garmin token endpoint failed ({(int)response.StatusCode}).");
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        if (string.IsNullOrEmpty(body?.AccessToken))
            throw new ProviderException(ProviderFailure.InvalidResponse, "Garmin returned no access token.");
        return new ServiceTokens(body.AccessToken, body.RefreshToken, clock.GetUtcNow().AddSeconds(Math.Max(0, body.ExpiresIn)), userId);
    }

    private async Task<string?> UserIdAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.UserIdUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.TryGetProperty("userId", out var id) ? id.ToString() : null;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] long ExpiresIn);
}
```

- [ ] **Step 5: Implement `GarminCourseDestination.cs`**

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meander.Core;

namespace Meander.Infrastructure;

public sealed record GarminGeoPoint(double Latitude, double Longitude, double? Elevation);
public sealed record GarminCourse(string CourseName, string Description, double Distance, double? ElevationGain,
    string ActivityType, string CoordinateSystem, IReadOnlyList<GarminGeoPoint> GeoPoints);

// The only place that knows Garmin's course payload.
public static class GarminCourseMapper
{
    public static GarminCourse Map(RouteCandidate route, Activity activity) => new(
        $"Meander {route.DistanceKm.ToString("F1", CultureInfo.InvariantCulture)} km",
        "Planned with Meander",
        Math.Round(route.DistanceKm * 1000, 1),
        route.ElevationGainM is { } gain ? Math.Round(gain, 1) : null,
        activity == Activity.Walking ? "WALKING" : "RUNNING",
        "WGS84",
        route.Geometry.Coordinates.Select(p => new GarminGeoPoint(p[1], p[0], p.Length > 2 ? p[2] : null)).ToArray());
}

public sealed class GarminCourseDestination(HttpClient http, GarminOptions options) : IRouteDestination
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ServiceKind Service => ServiceKind.Garmin;

    public async Task<SentRoute> SendAsync(RouteCandidate route, Activity activity, ServiceTokens tokens, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.CourseUrl)
        {
            Content = JsonContent.Create(GarminCourseMapper.Map(route, activity), options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await http.SendAsync(request, ct);
        // 403: the user did not grant the courses permission on Garmin's consent screen.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderException(ProviderFailure.Unauthorized, "Garmin rejected the connection.");
        if (!response.IsSuccessStatusCode)
            throw new ProviderException(ProviderFailure.Unavailable, $"Garmin rejected the course ({(int)response.StatusCode}).");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("courseId", out var value) ? value.ToString()
            : throw new ProviderException(ProviderFailure.InvalidResponse, "Garmin returned no course id.");
        return new SentRoute(id, new Uri($"https://connect.garmin.com/modern/course/{Uri.EscapeDataString(id)}"));
    }
}
```

- [ ] **Step 6: Wire it up in `Program.cs`**

Next to the other option instances, add:

```csharp
var garmin = new GarminOptions();
builder.Services.AddSingleton(garmin);
```

After the `ConnectionService` registration from Task 4, add:

```csharp
// Garmin calls are never retried: authorization codes are single-use and courses must not duplicate.
builder.Services.AddHttpClient<GarminConnector>(client => { client.Timeout = TimeSpan.FromSeconds(20); client.DefaultRequestHeaders.UserAgent.ParseAdd("Meander/1.0"); });
builder.Services.AddHttpClient<GarminCourseDestination>(client => { client.Timeout = TimeSpan.FromSeconds(20); client.DefaultRequestHeaders.UserAgent.ParseAdd("Meander/1.0"); });
builder.Services.AddTransient<IServiceConnector>(sp => sp.GetRequiredService<GarminConnector>());
builder.Services.AddTransient<IRouteDestination>(sp => sp.GetRequiredService<GarminCourseDestination>());
```

After the existing `Bind` calls, add the lines below. The `PublicBaseUrl` check from Task 4 must come after them.

```csharp
builder.Configuration.GetSection("Integrations:Garmin").Bind(garmin);
garmin.Validate();
```

The test fakes are registered later with `AddSingleton<IServiceConnector>`. They win because `ConnectionService.Connector` and the send endpoint take the last registered match.

- [ ] **Step 7: Add the webhook** to `MapConnectionEndpoints` in `ConnectionEndpoints.cs`

```csharp
// Garmin calls this when a user removes Meander in Garmin Connect. Always 200 so Garmin does not retry.
app.MapPost("/api/webhooks/garmin/deregistration", (GarminDeregistrations notice, IConnectionStore store, ILogger<ConnectionService> logger) =>
{
    var removed = (notice.Deregistrations ?? []).Where(d => !string.IsNullOrWhiteSpace(d.UserId))
        .Sum(d => store.DeleteByExternalUser(ServiceKind.Garmin, d.UserId!));
    logger.LogInformation("Garmin deregistration removed {Count} connection(s)", removed);
    return Results.Ok();
});
```

In `appsettings.json`, add `"Integrations": { "Garmin": { "ClientId": "", "ClientSecret": "" } },`.

- [ ] **Step 8: Run the tests and confirm they pass**

Run: `dotnet test Meander.slnx`
Expected: every test passes, including the 8 cases in `GarminTests`. The existing tests have no Garmin `ClientId`, so `/api/connections` stays empty there and startup does not require `PublicBaseUrl`.

- [ ] **Step 9: Commit**

```bash
git add src tests
git commit -m "Connect Garmin and send routes as Garmin courses"
```

---

### Task 6: Frontend API and the FIT download

**Files:**
- Modify: `frontend/src/types.ts`, `frontend/src/api.ts`, `frontend/src/App.tsx`, `frontend/src/styles.css`, `frontend/src/locales/en.ts`, `frontend/src/locales/nl.ts`
- Test: `frontend/src/api.test.ts`, `frontend/src/App.test.tsx`

**Interfaces:**
- Produces in `types.ts`:

```ts
export type ServiceKind = 'garmin';
export type ServiceConnectionStatus = { service: ServiceKind; connected: boolean };
export type SentRoute = { externalId: string; viewUrl: string | null };
```

- Produces in `api.ts`:
  - `downloadFit(candidate, signal)`
  - `listConnections(signal?)`
  - `connectUrl(service, returnUrl)`
  - `disconnect(service)`
  - `sendRoute(id, service, signal)`
- Produces in `App.tsx`: `exportRoute(candidate: RouteCandidate, format: 'gpx' | 'fit' = 'gpx')`.

- [ ] **Step 1: Write the failing tests**

`api.test.ts`. Follow the file's existing `fetch` mocking style:

```ts
import { connectUrl, disconnect, listConnections, sendRoute } from './api';

test('builds connect URLs and calls the connection endpoints', async () => {
  expect(connectUrl('garmin', '/routes/s?route=r')).toBe('/api/connections/garmin/authorize?returnUrl=%2Froutes%2Fs%3Froute%3Dr');
  const fetchMock = vi.fn()
    .mockResolvedValueOnce(new Response(JSON.stringify([{ service: 'garmin', connected: true }]), { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ externalId: '1', viewUrl: null }), { status: 200 }))
    .mockResolvedValueOnce(new Response(null, { status: 204 }));
  vi.stubGlobal('fetch', fetchMock);
  expect(await listConnections()).toEqual([{ service: 'garmin', connected: true }]);
  expect(await sendRoute('route-1', 'garmin', new AbortController().signal)).toEqual({ externalId: '1', viewUrl: null });
  expect(fetchMock).toHaveBeenNthCalledWith(2, '/api/routes/route-1/send/garmin', expect.objectContaining({ method: 'POST' }));
  await disconnect('garmin');
  expect(fetchMock).toHaveBeenNthCalledWith(3, '/api/connections/garmin', expect.objectContaining({ method: 'DELETE' }));
  vi.unstubAllGlobals();
});
```

Changes to `App.test.tsx`:
- Add `downloadFit`, `listConnections`, `sendRoute` and `disconnect` to the `./api` import and to the `vi.mock('./api', …)` factory as `downloadFit: vi.fn(), sendRoute: vi.fn(), disconnect: vi.fn(), listConnections: vi.fn()`.
- In `beforeEach`, add `vi.mocked(downloadFit).mockReset(); vi.mocked(sendRoute).mockReset(); vi.mocked(listConnections).mockReset().mockResolvedValue([]);`.
- Add this test:

```tsx
it('downloads a FIT course for the chosen route', async () => {
  vi.mocked(generate).mockResolvedValue([candidate(1), candidate(2)]);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await userEvent.click(await screen.findByRole('button', { name: /Route 2/ }));
  await userEvent.click(screen.getAllByRole('button', { name: /Download FIT/ })[1]);
  expect(downloadFit).toHaveBeenCalledWith(expect.objectContaining({ id: 'route-2' }), expect.any(AbortSignal));
});
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `npm test -- api.test App.test`
Expected: they fail on missing exports (`connectUrl`, `downloadFit`).

- [ ] **Step 3: Implement**

`types.ts`: add the three types from the Interfaces block.

`api.ts`: add `ServiceConnectionStatus`, `ServiceKind` and `SentRoute` to the type import. Replace `downloadGpx` with:

```ts
async function downloadRoute(candidate: RouteCandidate, format: 'gpx' | 'fit', signal: AbortSignal) {
  const response = await checked(await fetch(`/api/routes/${candidate.id}/${format}`, { signal }));
  const url = URL.createObjectURL(await response.blob());
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `meander-${candidate.distanceKm.toFixed(1)}km.${format}`;
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
export const downloadGpx = (candidate: RouteCandidate, signal: AbortSignal) => downloadRoute(candidate, 'gpx', signal);
export const downloadFit = (candidate: RouteCandidate, signal: AbortSignal) => downloadRoute(candidate, 'fit', signal);
export async function listConnections(signal?: AbortSignal): Promise<ServiceConnectionStatus[]> {
  return (await checked(await fetch('/api/connections', { signal }))).json() as Promise<ServiceConnectionStatus[]>;
}
export const connectUrl = (service: ServiceKind, returnUrl: string) =>
  `/api/connections/${service}/authorize?returnUrl=${encodeURIComponent(returnUrl)}`;
export async function disconnect(service: ServiceKind) {
  await checked(await fetch(`/api/connections/${service}`, { method: 'DELETE' }));
}
export async function sendRoute(id: string, service: ServiceKind, signal: AbortSignal): Promise<SentRoute> {
  return (await checked(await fetch(`/api/routes/${encodeURIComponent(id)}/send/${service}`, { method: 'POST', signal }))).json() as Promise<SentRoute>;
}
```

In `App.tsx`:
- Import `downloadFit`.
- Replace `exportRoute` with the version below. It keeps the multilingual error handling.
- In the route card, directly after the closing `</div>` of `.route-card-actions`, add `<div className="route-card-more">…</div>`.

```tsx
async function exportRoute(candidate: RouteCandidate, format: 'gpx' | 'fit' = 'gpx') {
  exportingController.current?.abort();
  const controller = new AbortController(); exportingController.current = controller;
  setExporting(candidate.id); setError([]);
  try { await (format === 'fit' ? downloadFit : downloadGpx)(candidate, controller.signal); }
  catch (ex) {
    if (!controller.signal.aborted) setError(ex instanceof ApiError && ex.problem.status === 404
      ? [{ key: 'errors.exportExpired' }] : failureMessages(ex, 'error.exportFailed', number));
  } finally { if (!controller.signal.aborted) setExporting(null); }
}
```

```tsx
<div className="route-card-more">
  <button type="button" className="text-link" disabled={exporting !== null} onClick={() => void exportRoute(candidate, 'fit')}>
    <Icon name="download" />{t('route.downloadFit')}</button>
</div>
```

`styles.css`:
- After `.route-card-actions { … }`, add `.route-card-more { display:flex; flex-wrap:wrap; gap:4px 18px; margin-top:4px; }`.
- In the mobile rule at line ~522, extend the selector to `:is(.route-card-actions, .route-card-more, .route-quality, .route-warnings)`.

Locales: in `en.ts` add `'route.downloadFit': 'Download FIT',`; in `nl.ts` add `'route.downloadFit': 'FIT downloaden',`.

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `npm test && npx tsc -b`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add frontend/src
git commit -m "Download routes as FIT courses"
```

---

### Task 7: Send to Garmin, connections panel and the return flow

**Files:**
- Create: `frontend/src/useConnections.ts`, `frontend/src/connectReturn.ts`, `frontend/src/components/ConnectionsPanel.tsx`
- Modify: `frontend/src/App.tsx`, `frontend/src/components/Icon.tsx`, `frontend/src/styles.css`, `frontend/src/locales/en.ts`, `frontend/src/locales/nl.ts`
- Test: `frontend/src/connectReturn.test.ts`, `frontend/src/App.test.tsx`

**Interfaces:**
- Consumes: `listConnections`, `connectUrl`, `disconnect`, `sendRoute` (Task 6), `useI18n`, `failureMessages`.
- Produces:
  - `export const SERVICE_NAMES: Record<ServiceKind, string>` in `useConnections.ts`
  - `export function useConnections(): { connections: ServiceConnectionStatus[]; setConnected(service: ServiceKind, connected: boolean): void; disconnect(service: ServiceKind): Promise<void> }`
  - `export type ConnectReturn = { service: ServiceKind; failed: boolean }`
  - `export function readConnectReturn(search: string): ConnectReturn | null`
  - `ConnectionsPanel` props: `{ connections: ServiceConnectionStatus[]; returnUrl: string; onDisconnect(service: ServiceKind): void }`

- [ ] **Step 1: Write the failing tests**

`frontend/src/connectReturn.test.ts`:

```ts
import { expect, test } from 'vitest';
import { readConnectReturn } from './connectReturn';

test('reads the service a visitor returns from', () => {
  expect(readConnectReturn('?route=r&connected=garmin')).toEqual({ service: 'garmin', failed: false });
  expect(readConnectReturn('?connectError=garmin')).toEqual({ service: 'garmin', failed: true });
  expect(readConnectReturn('?connected=strava')).toBeNull();
  expect(readConnectReturn('?route=r')).toBeNull();
});
```

`App.test.tsx`. Add `connectUrl` to the imports; it stays the real implementation because the mock spreads `actual`.

```tsx
it('sends a route to a connected Garmin account', async () => {
  vi.mocked(listConnections).mockResolvedValue([{ service: 'garmin', connected: true }]);
  vi.mocked(sendRoute).mockResolvedValue({ externalId: '1', viewUrl: null });
  vi.mocked(generate).mockResolvedValue(savedSearch().candidates);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await userEvent.click((await screen.findAllByRole('button', { name: /Send to Garmin/ }))[0]);
  expect(sendRoute).toHaveBeenCalledWith('route-1', 'garmin', expect.any(AbortSignal));
  expect(await screen.findByText(/Route sent to Garmin/)).toBeInTheDocument();
  expect(screen.getByText('Garmin connected')).toBeInTheDocument();
});

it('links to Garmin when it is not connected yet', async () => {
  vi.mocked(listConnections).mockResolvedValue([{ service: 'garmin', connected: false }]);
  vi.mocked(generate).mockResolvedValue(savedSearch().candidates);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  const link = (await screen.findAllByRole('link', { name: /Send to Garmin/ }))[0];
  expect(link).toHaveAttribute('href', connectUrl('garmin', '/routes/search-1?route=route-1'));
  expect(sendRoute).not.toHaveBeenCalled();
});

it('sends the route automatically after returning from Garmin', async () => {
  window.history.replaceState(null, '', '/routes/search-1?route=route-2&connected=garmin');
  vi.mocked(loadSearch).mockResolvedValue(savedSearch('search-1'));
  vi.mocked(listConnections).mockResolvedValue([{ service: 'garmin', connected: true }]);
  vi.mocked(sendRoute).mockResolvedValue({ externalId: '1', viewUrl: null });
  render(<App />);
  await waitFor(() => expect(sendRoute).toHaveBeenCalledWith('route-2', 'garmin', expect.any(AbortSignal)));
  expect(window.location.search).toBe('?route=route-2');
});

it('reports a cancelled connection and an expired one', async () => {
  window.history.replaceState(null, '', '/routes/search-1?route=route-1&connectError=garmin');
  vi.mocked(loadSearch).mockResolvedValue(savedSearch('search-1'));
  vi.mocked(listConnections).mockResolvedValue([{ service: 'garmin', connected: true }]);
  vi.mocked(sendRoute).mockRejectedValue(new ApiError({ status: 401, code: 'connectionExpired' }));
  render(<App />);
  expect(await screen.findByRole('alert')).toHaveTextContent('The connection was cancelled');
  await userEvent.click((await screen.findAllByRole('button', { name: /Send to Garmin/ }))[0]);
  expect(await screen.findByRole('alert')).toHaveTextContent('The connection has expired');
  expect((await screen.findAllByRole('link', { name: /Send to Garmin/ }))[0]).toBeInTheDocument();
});
```

- [ ] **Step 2: Run the tests and confirm they fail**

Run: `npm test -- connectReturn App.test`
Expected: they fail because the module is missing and the "Send to Garmin" control is missing.

- [ ] **Step 3: Implement the helpers**

`frontend/src/connectReturn.ts`:

```ts
import type { ServiceKind } from './types';

const SERVICES: readonly ServiceKind[] = ['garmin'];
export type ConnectReturn = { service: ServiceKind; failed: boolean };

// The API returns from a service's consent screen with ?connected= or ?connectError=.
export function readConnectReturn(search: string): ConnectReturn | null {
  const params = new URLSearchParams(search);
  const failedService = params.get('connectError');
  const service = (failedService ?? params.get('connected')) as ServiceKind | null;
  return service && SERVICES.includes(service) ? { service, failed: failedService !== null } : null;
}
```

`frontend/src/useConnections.ts`:

```ts
import { useCallback, useEffect, useState } from 'react';
import { disconnect as disconnectService, listConnections } from './api';
import type { ServiceConnectionStatus, ServiceKind } from './types';

export const SERVICE_NAMES: Record<ServiceKind, string> = { garmin: 'Garmin' };

export function useConnections() {
  const [connections, setConnections] = useState<ServiceConnectionStatus[]>([]);
  useEffect(() => {
    const controller = new AbortController();
    // Integrations are optional: an unreachable list just hides them.
    listConnections(controller.signal).then(list => setConnections(list ?? [])).catch(() => { if (!controller.signal.aborted) setConnections([]); });
    return () => controller.abort();
  }, []);
  const setConnected = useCallback((service: ServiceKind, connected: boolean) =>
    setConnections(list => list.map(item => item.service === service ? { ...item, connected } : item)), []);
  const disconnect = useCallback(async (service: ServiceKind) => {
    await disconnectService(service);
    setConnected(service, false);
  }, [setConnected]);
  return { connections, setConnected, disconnect };
}
```

`frontend/src/components/ConnectionsPanel.tsx`:

```tsx
import { connectUrl } from '../api';
import { useI18n } from '../i18n';
import type { ServiceConnectionStatus, ServiceKind } from '../types';
import { SERVICE_NAMES } from '../useConnections';
import { Icon } from './Icon';

export function ConnectionsPanel({ connections, returnUrl, onDisconnect }: {
  connections: ServiceConnectionStatus[]; returnUrl: string; onDisconnect(service: ServiceKind): void;
}) {
  const { t } = useI18n();
  if (connections.length === 0) return null;
  return <section className="connections" aria-label={t('connections.title')}>
    {connections.map(({ service, connected }) => <p key={service} className="export-note">
      <Icon name="send" />
      {connected ? <>
        <span>{t('connections.connected', { service: SERVICE_NAMES[service] })}</span>
        <button type="button" className="text-link" onClick={() => onDisconnect(service)}>{t('connections.disconnect')}</button>
      </> : <a href={connectUrl(service, returnUrl)}>{t('connections.connect', { service: SERVICE_NAMES[service] })}</a>}
    </p>)}
  </section>;
}
```

`Icon.tsx`: add `| 'send'` to `IconName` and this entry to `shapes`:

```tsx
send: <><path d="M21 3 10 14" /><path d="m21 3-7 18-4-7-7-4 18-7Z" /></>,
```

- [ ] **Step 4: Wire it into `App.tsx`** (inside `Planner`, after the multilingual work)

Imports: add `connectUrl`, `sendRoute` from `./api`; `ConnectionsPanel` from `./components/ConnectionsPanel`; `readConnectReturn` from `./connectReturn`; `SERVICE_NAMES` and `useConnections` from `./useConnections`; `ServiceKind` from `./types`.

State, next to `exporting`:

```tsx
const { connections, setConnected, disconnect } = useConnections();
const [sending, setSending] = useState<string | null>(null);
// Read before the saved-search restore rewrites the URL.
const [connectReturn, setConnectReturn] = useState(() => readConnectReturn(window.location.search));
```

Functions, after `exportRoute`:

```tsx
async function sendToService(candidate: RouteCandidate, service: ServiceKind) {
  const controller = new AbortController();
  setSending(candidate.id); setError([]); setNotice(null);
  try {
    await sendRoute(candidate.id, service, controller.signal);
    setConnected(service, true);
    setNotice({ key: 'notice.sent', params: { service: SERVICE_NAMES[service] } });
  } catch (ex) {
    if (ex instanceof ApiError && ex.problem.status === 401) setConnected(service, false);
    setError(failureMessages(ex, 'error.sendFailed', number));
  } finally { setSending(null); }
}
async function disconnectService(service: ServiceKind) {
  try { await disconnect(service); } catch (ex) { setError(failureMessages(ex, 'error.disconnectFailed', number)); }
}

useEffect(() => {
  if (!connectReturn || phase !== 'done') return;
  setConnectReturn(null);
  if (connectReturn.failed) { setError([{ key: 'errors.connectDenied' }]); return; }
  const route = candidates.find(item => item.id === selectedId);
  if (route) void sendToService(route, connectReturn.service);
}, [connectReturn, phase, candidates, selectedId]);
```

Inside `.route-card-more` (from Task 6), after the FIT button:

```tsx
{candidate.searchId && connections.map(({ service, connected }) => {
  const label = <><Icon name="send" />{sending === candidate.id ? t('route.sending') : t('route.sendTo', { service: SERVICE_NAMES[service] })}</>;
  return connected
    ? <button key={service} type="button" className="text-link" disabled={sending !== null} onClick={() => void sendToService(candidate, service)}>{label}</button>
    : <a key={service} className="text-link" href={connectUrl(service, savedSearchPath(candidate.searchId!, candidate.id))}>{label}</a>;
})}
```

Directly after the `export-note` paragraph under the results list, add:

```tsx
{candidates[0].searchId && <ConnectionsPanel connections={connections}
  returnUrl={savedSearchPath(candidates[0].searchId, selectedId ?? candidates[0].id)} onDisconnect={service => void disconnectService(service)} />}
```

`styles.css`: `.connections { display:grid; gap:4px; margin-top:6px; }`.

Locales:

| key | en | nl |
|---|---|---|
| `route.sendTo` | `Send to {service}` | `Naar {service} sturen` |
| `route.sending` | `Sending…` | `Versturen…` |
| `notice.sent` | `Route sent to {service}. Sync your device to load it.` | `Route naar {service} gestuurd. Synchroniseer je toestel om hem te laden.` |
| `connections.title` | `Connected services` | `Gekoppelde diensten` |
| `connections.connected` | `{service} connected` | `{service} gekoppeld` |
| `connections.connect` | `Connect {service}` | `{service} koppelen` |
| `connections.disconnect` | `Disconnect` | `Ontkoppelen` |
| `error.sendFailed` | `Could not send the route. Please try again.` | `De route kon niet worden verstuurd. Probeer het opnieuw.` |
| `error.disconnectFailed` | `Could not disconnect. Please try again.` | `Ontkoppelen is mislukt. Probeer het opnieuw.` |
| `errors.connectDenied` | `The connection was cancelled. Nothing was sent.` | `Het koppelen is geannuleerd. Er is niets verstuurd.` |

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `npm test && npx tsc -b`
Expected: all pass.

If the "sends automatically" test sees `?route=route-2&connected=garmin`, the restore effect ran after the test read the URL. Assert with `await waitFor(() => expect(window.location.search).toBe('?route=route-2'))`.

- [ ] **Step 6: Commit**

```bash
git add frontend/src
git commit -m "Send routes to Garmin from the route card"
```

---

### Task 8: End-to-end Garmin flow

**Files:**
- Modify: `frontend/e2e/upstream.mjs`, `frontend/playwright.config.ts`, `frontend/e2e/route.spec.ts`

**Interfaces:**
- Consumes: the whole backend and frontend flow.
- The fake Garmin server lives at `http://127.0.0.1:5091/garmin/*`, inside the existing upstream fixture server.

- [ ] **Step 1: Add the fake Garmin endpoints** to `upstream.mjs`, before its final 404 fallback:

```js
  if (path === '/garmin/authorize') {
    const params = new URL(req.url, 'http://127.0.0.1:5091').searchParams;
    const back = new URL(params.get('redirect_uri'));
    back.searchParams.set('code', 'e2e-code');
    back.searchParams.set('state', params.get('state'));
    res.statusCode = 302; res.setHeader('Location', back.toString()); res.end(); return;
  }
  if (path === '/garmin/token') { res.end(JSON.stringify({ access_token: 'e2e-access', refresh_token: 'e2e-refresh', expires_in: 86400 })); return; }
  if (path === '/garmin/user/id') { res.end(JSON.stringify({ userId: 'e2e-user' })); return; }
  if (path === '/garmin/user/registration') { res.statusCode = 204; res.end(); return; }
  if (path === '/garmin/course') { garminCourses++; res.end(JSON.stringify({ courseId: 4242 })); return; }
  if (path === '/garmin/courses') { res.end(JSON.stringify({ count: garminCourses })); return; }
```

Declare `let garminCourses = 0;` next to `let calls = 0;`.

- [ ] **Step 2: Configure the API** in `playwright.config.ts`, by adding to the API `webServer.env`:

```ts
PublicBaseUrl: 'http://127.0.0.1:5174',
Integrations__Garmin__ClientId: 'e2e-client', Integrations__Garmin__ClientSecret: 'e2e-secret',
Integrations__Garmin__AuthorizeUrl: 'http://127.0.0.1:5091/garmin/authorize',
Integrations__Garmin__TokenUrl: 'http://127.0.0.1:5091/garmin/token',
Integrations__Garmin__UserIdUrl: 'http://127.0.0.1:5091/garmin/user/id',
Integrations__Garmin__RegistrationUrl: 'http://127.0.0.1:5091/garmin/user/registration',
Integrations__Garmin__CourseUrl: 'http://127.0.0.1:5091/garmin/course',
DataProtection__KeysPath: '',
```

The Vite proxy keeps the `127.0.0.1:5174` host header. That host matches `PublicBaseUrl` and is already in the default `AllowedHosts`, so authorize does not redirect and the cookies stay on one host.

- [ ] **Step 3: Write the e2e test** in `route.spec.ts`. Reuse the start/search steps from the existing "choose a start, generate and inspect routes, and export GPX" test:

```ts
test('connect Garmin and send the chosen route', async ({ page, request }) => {
  await page.route('https://fonts.googleapis.com/**', route => route.abort());
  await page.goto('/');
  await chooseUtrecht(page);
  await page.getByRole('button', { name: /Next: distance/ }).click();
  await page.getByRole('button', { name: /Next: preferences/ }).click();
  await page.getByRole('button', { name: 'Find my routes' }).click();
  await expect(page.getByRole('button', { name: /Route [123]/ })).toHaveCount(3);
  await page.locator('.route-card.selected').getByRole('link', { name: /Send to Garmin/ }).click();
  await expect(page.getByRole('status')).toContainText('Route sent to Garmin');
  await expect(page.getByText('Garmin connected')).toBeVisible();
  expect(page.url()).not.toContain('connected=');
  expect((await (await request.get('http://127.0.0.1:5091/garmin/courses')).json()).count).toBe(1);
  await page.getByRole('button', { name: 'Disconnect' }).click();
  await expect(page.getByRole('link', { name: 'Connect Garmin' })).toBeVisible();
});
```

`chooseUtrecht` is the existing helper in `route.spec.ts` that the GPX export test uses. The link sends the browser through the fake authorize endpoint and the API callback, then back to the saved route, where the app sends it automatically.

- [ ] **Step 4: Run it**

Run: `npm run test:e2e -- --grep "Garmin"`
Expected: PASS. Then run the full `npm run test:e2e`; it must pass too.

- [ ] **Step 5: Commit**

```bash
git add frontend/e2e frontend/playwright.config.ts
git commit -m "Cover the Garmin connect-and-send flow end to end"
```

---

### Task 9: Configuration, deployment and docs

**Files:**
- Modify: `deploy/unraid/meander/docker-compose.yml`, `README.md`

- [ ] **Step 1: Compose.** In the `meander` service `environment`, add:

```yaml
      # Garmin Connect integration; empty client id = off. Values come from the stack's .env (not in git).
      PublicBaseUrl: https://meander.roesink.dev
      Integrations__Garmin__ClientId: ${GARMIN_CLIENT_ID:-}
      Integrations__Garmin__ClientSecret: ${GARMIN_CLIENT_SECRET:-}
```

Data Protection keys go to `/app/data/keys`, through the default `data/keys` relative to `/app`. That is on the existing `/mnt/user/appdata/meander/data` volume, so tokens survive container rebuilds.

- [ ] **Step 2: README.** Add an "Integrations" section after "Structure and extension":

```markdown
## Integrations

Meander can send a route to a visitor's own Garmin Connect account as a course. Connections are tied to an anonymous browser cookie (`meander_sid`); there are no accounts. Tokens are encrypted with ASP.NET Data Protection, using keys in `data/keys` next to the SQLite database.

Every route can also be downloaded as a FIT course (`/api/routes/{id}/fit`), which works without any integration.

### Garmin

1. Apply for the [Garmin Connect Developer Program](https://developer.garmin.com/gc-developer-program/overview) with the Courses API. Privacy policy: https://www.roesink.dev/privacy/
2. Register these URLs in the Garmin developer portal:
   - Redirect URI: `https://meander.roesink.dev/api/connections/garmin/callback`
   - Deregistration notification: `https://meander.roesink.dev/api/webhooks/garmin/deregistration`
3. Set `Integrations__Garmin__ClientId`, `Integrations__Garmin__ClientSecret` and `PublicBaseUrl`. On Unraid, set them as `GARMIN_CLIENT_ID` and `GARMIN_CLIENT_SECRET` in the `meander` stack's `.env`.

Garmin's URLs are configurable under `Integrations:Garmin` (`AuthorizeUrl`, `TokenUrl`, `UserIdUrl`, `RegistrationUrl`, `CourseUrl`). The course payload is built in one place, `GarminCourseMapper`.

### Adding a service

- A connection and what a service can do are separate. Implement `IServiceConnector` (OAuth: authorize URL, code exchange, refresh, revoke) for the service.
- Implement one capability per feature, for example `IRouteDestination` to push routes.
- Add the service to `ServiceKind` and register both in `Program.cs`.
- A future data source (such as Strava activities) gets its own capability interface and takes its tokens from `ConnectionService.UseAsync`. The connection layer does not change.
```

In the `README.md` API endpoint list (if present), add the six endpoints from Task 4 and `/fit`.

- [ ] **Step 3: Full verification**

Run: `dotnet test Meander.slnx`, then `cd frontend && npm test && npx tsc -b && npm run build && npm run test:e2e`
Expected: every check passes.

- [ ] **Step 4: Commit**

```bash
git add README.md deploy/unraid/meander/docker-compose.yml
git commit -m "Document and configure the Garmin integration"
```

- [ ] **Step 5: Server .env.** This is an operational step, done when Garmin credentials exist. Use the `unraid` skill to find the directory Compose Manager runs the `meander` stack from. Put `GARMIN_CLIENT_ID=` and `GARMIN_CLIENT_SECRET=` in the `.env` there. Leave them empty until Garmin approves the application.
