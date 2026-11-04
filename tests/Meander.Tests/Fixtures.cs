using Meander.Core;

namespace Meander.Tests;

internal static class Fixtures
{
    public static readonly Location Start = new(52.09, 5.12);
    public static RouteRequest Loop(double distance = 5, double? radius = null) => new(Start, null, radius, distance);
    public static RouteResult Petal(double bearing = 0, double extent = 1, double distance = 5) =>
        new([Start, Geo.Destination(Start, bearing, extent), Geo.Destination(Start, bearing + 65, extent), Start], distance, 12);
    public static RouteCandidate Candidate(DateTimeOffset? expires = null) => new(
        Guid.NewGuid(), expires ?? DateTimeOffset.UtcNow.AddMinutes(30),
        LineString.From([Start with { ElevationM = 12.5 }, Geo.Destination(Start, 90, 1), Start]),
        2, 12.5, 1, .01, CandidateStatus.Qualified, new(Start, Start, 0), new(Start, Start, 0), []);
}

internal sealed class MemoryStore : ICandidateStore
{
    public readonly Dictionary<Guid, RouteCandidate> Items = [];
    public void Put(RouteCandidate candidate) => Items[candidate.Id] = candidate;
    public RouteCandidate? Get(Guid id) => Items.GetValueOrDefault(id);
    private readonly Dictionary<Guid, RouteDraft> drafts = [];
    public void PutDraft(Guid id, RouteDraft draft, DateTimeOffset expiresAt) => drafts[id] = draft;
    public RouteDraft? GetDraft(Guid id) => drafts.GetValueOrDefault(id);
}
internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class FixedWoodland(WoodlandMap? map) : IWoodlandProvider
{
    public int Calls;
    public Activity? LastActivity;
    public Task<WoodlandMap?> FindAsync(Location center, double radiusKm, Activity activity, CancellationToken cancellationToken)
    { Calls++; LastActivity = activity; return Task.FromResult(map); }
}
internal sealed class FakeRouting : IRoutingProvider
{
    public int Rounds;
    public readonly List<ViaPointsQuery> ViaQueries = [];
    public Func<RoundTripQuery, CancellationToken, Task<RouteResult>> Round { get; set; } =
        (q, _) => Task.FromResult(Fixtures.Petal(q.Seed % 360, distance: q.DistanceKm));
    public Func<ViaPointsQuery, CancellationToken, Task<RouteResult>> Via { get; set; } =
        (q, _) => Task.FromResult(new RouteResult(q.Points, q.Shortest ? 2 : 5, 0));
    public Func<IReadOnlyList<Location>, IReadOnlyList<SnapResult?>> Snap { get; set; } =
        points => points.Select(p => (SnapResult?)new(p, 0)).ToArray();
    public Task<IReadOnlyList<SnapResult?>> SnapAsync(IReadOnlyList<Location> points, Activity activity, double radiusM, CancellationToken ct)
        => Task.FromResult(Snap(points));
    public Task<RouteResult> RoundTripAsync(RoundTripQuery query, CancellationToken ct)
    { Interlocked.Increment(ref Rounds); return Round(query, ct); }
    public Task<RouteResult> ViaPointsAsync(ViaPointsQuery query, CancellationToken ct)
    { lock (ViaQueries) ViaQueries.Add(query); return Via(query, ct); }
}
