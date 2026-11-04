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

    private sealed record PendingAuthorization(Guid SessionId, string State, string Verifier, ServiceKind Service, string ReturnUrl, DateTimeOffset ExpiresAt);

    public static void MapConnectionEndpoints(this WebApplication app)
    {
        // Garmin calls this when a user removes Meander in Garmin Connect. Always 200 so Garmin does not retry.
        app.MapPost("/api/webhooks/garmin/deregistration", (GarminDeregistrations notice, IConnectionStore store, ILogger<ConnectionService> logger) =>
        {
            var removed = (notice.Deregistrations ?? []).Where(d => !string.IsNullOrWhiteSpace(d.UserId))
                .Sum(d => store.DeleteByExternalUser(ServiceKind.Garmin, d.UserId!));
            logger.LogInformation("Garmin deregistration removed {Count} connection(s)", removed);
            return Results.Ok();
        });

        app.MapGet("/api/connections", (HttpContext context, ConnectionService connections, IConnectionStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var connected = SessionId(context) is { } session ? store.List(session).Select(c => c.Service).ToHashSet() : [];
            return Results.Ok(connections.Configured.Select(service => new { service, connected = connected.Contains(service) }));
        });

        app.MapGet("/api/connections/{service}/authorize", (string service, string? returnUrl, HttpContext context,
            ConnectionService connections, IConnectionStore store, ConnectionOptions options, IDataProtectionProvider protection, TimeProvider clock) =>
        {
            if (Connector(service, connections) is not { } connector) return ServiceNotFound();
            // Cookies are per host, so the flow must start on the host the callback returns to.
            var publicBase = new Uri(options.PublicBaseUrl);
            if (!string.Equals(context.Request.Host.Host, publicBase.Host, StringComparison.OrdinalIgnoreCase))
                return Results.Redirect(new Uri(publicBase, context.Request.Path + context.Request.QueryString).ToString());
            // A client-chosen sid is only trusted while it already owns connections; otherwise issue a fresh one.
            var sid = SessionId(context) is { } existing && store.List(existing).Count > 0 ? existing : Guid.NewGuid();
            if (SessionId(context) != sid)
                context.Response.Cookies.Append(SessionCookie, sid.ToString(), Cookie(options, SessionLifetime));
            var pkce = OAuthPkce.Create();
            var pending = new PendingAuthorization(sid, pkce.State, pkce.Verifier, connector.Service, LocalPath(returnUrl), clock.GetUtcNow() + PendingLifetime);
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
            if (pending is null || SessionId(context) is not { } session || pending.SessionId != session || pending.Service != connector.Service ||
                pending.ExpiresAt < clock.GetUtcNow() || state is null ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(pending.State)))
                return Results.Problem(statusCode: 400, title: "Connection could not be completed",
                    detail: "Start connecting again from Meander.", extensions: Code("connectionStateInvalid"));
            var failed = QueryHelpers.AddQueryString(pending.ReturnUrl, "connectError", Name(connector.Service));
            if (error is not null || string.IsNullOrEmpty(code)) return Results.Redirect(failed);
            try
            {
                var tokens = await connector.ExchangeCodeAsync(code, pending.Verifier, RedirectUri(options, connector.Service), ct);
                // Rotate the session so the sid that now holds tokens was never chosen by the client.
                var rotated = Guid.NewGuid();
                foreach (var held in store.List(session))
                {
                    store.Upsert(held with { SessionId = rotated });
                    store.Delete(session, held.Service);
                }
                store.Upsert(new ServiceConnection(rotated, connector.Service, tokens, clock.GetUtcNow()));
                context.Response.Cookies.Append(SessionCookie, rotated.ToString(), Cookie(options, SessionLifetime));
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
            catch (ProviderException ex) when (ex.Failure == ProviderFailure.Unauthorized)
            {
                return Results.Problem(statusCode: 401, title: "Connection expired",
                    detail: $"Connect {connector.Service} again to send this route.", extensions: Code("connectionExpired"));
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException ||
                                       ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Sending a route to {Service} failed: {Failure}", connector.Service, ex.GetType().Name);
                return Results.Problem(statusCode: 502, title: "Service unavailable",
                    detail: $"{connector.Service} did not accept the route. Try again shortly.", extensions: Code("serviceUnavailable"));
            }
        }).RequireRateLimiting("connect");
    }

    public static Guid? SessionId(HttpContext context) =>
        Guid.TryParse(context.Request.Cookies[SessionCookie], out var id) ? id : null;

    public static string LocalPath(string? returnUrl) =>
        returnUrl is { Length: > 0 } && returnUrl[0] == '/' && (returnUrl.Length == 1 || returnUrl[1] is not ('/' or '\\')) &&
        !returnUrl.Any(char.IsControl) ? returnUrl : "/";

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
