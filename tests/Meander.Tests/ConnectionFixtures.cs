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
            foreach (var key in keys) { Items.Remove(key); SentItems.RemoveAll(s => s.Session == key.Item1 && s.Record.Service == service); }
            return keys.Length;
        }
    }
    public readonly List<(Guid Session, SentRouteRecord Record)> SentItems = [];
    public void RecordSent(Guid sessionId, SentRouteRecord sent)
    {
        lock (Items) { SentItems.RemoveAll(s => s.Session == sessionId && s.Record.Service == sent.Service && s.Record.RouteId == sent.RouteId); SentItems.Add((sessionId, sent)); }
    }
    public IReadOnlyList<SentRouteRecord> ListSent(Guid sessionId) { lock (Items) return SentItems.Where(s => s.Session == sessionId).Select(s => s.Record).ToArray(); }
    public void MoveSent(Guid from, Guid to) { lock (Items) for (var i = 0; i < SentItems.Count; i++) if (SentItems[i].Session == from) SentItems[i] = (to, SentItems[i].Record); }
    public void DeleteSent(Guid sessionId, ServiceKind service) { lock (Items) SentItems.RemoveAll(s => s.Session == sessionId && s.Record.Service == service); }
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
    public Task<SentRoute> SendAsync(RouteCandidate route, Activity activity, Uri? routeUrl, ServiceTokens tokens, CancellationToken ct)
    {
        if (Reject) throw new ProviderException(ProviderFailure.Unauthorized, "token revoked");
        Sent.Add((route, activity, tokens));
        return Task.FromResult(new SentRoute("course-1", new Uri("https://provider.test/course/1")));
    }
}
