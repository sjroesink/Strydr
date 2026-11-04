using System.Collections.Concurrent;

namespace Meander.Core;

// A connection (who you are at a service) is separate from what a service can do,
// so pulling data later (an activity source) reuses the same tokens and refresh logic.
public enum ServiceKind { Garmin }
public sealed record ServiceTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? ExternalUserId);
public sealed record ServiceConnection(Guid SessionId, ServiceKind Service, ServiceTokens Tokens, DateTimeOffset ConnectedAt);
public sealed record SentRoute(string ExternalId, Uri? ViewUrl);
// Kept per session so a sent route still links to the service after a reload.
public sealed record SentRouteRecord(Guid RouteId, ServiceKind Service, SentRoute Sent, DateTimeOffset SentAt);

public interface IConnectionStore
{
    ServiceConnection? Get(Guid sessionId, ServiceKind service);
    IReadOnlyList<ServiceConnection> List(Guid sessionId);
    void Upsert(ServiceConnection connection);
    void Delete(Guid sessionId, ServiceKind service);
    // Also forgets what those sessions sent: the service no longer knows them.
    int DeleteByExternalUser(ServiceKind service, string externalUserId);
    void RecordSent(Guid sessionId, SentRouteRecord sent);
    IReadOnlyList<SentRouteRecord> ListSent(Guid sessionId);
    void MoveSent(Guid fromSessionId, Guid toSessionId);
    void DeleteSent(Guid sessionId, ServiceKind service);
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
    // routeUrl opens the route in Meander again; null when the route was never saved.
    Task<SentRoute> SendAsync(RouteCandidate route, Activity activity, Uri? routeUrl, ServiceTokens tokens, CancellationToken ct);
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
        catch (ProviderException ex) when (ex.Failure == ProviderFailure.Unauthorized) { store.Delete(sessionId, service); throw; }
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
            catch (ProviderException ex) when (ex.Failure == ProviderFailure.Unauthorized) { store.Delete(sessionId, service); throw; }
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
        store.DeleteSent(sessionId, service);
        return connection;
    }

    private bool Fresh(ServiceTokens tokens) => tokens.ExpiresAt - clock.GetUtcNow() > RefreshMargin;
    private static ProviderException NotConnected(ServiceKind service) => new(ProviderFailure.Unauthorized, $"Connect {service} first.");
}
