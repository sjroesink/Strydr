# Service integrations: Garmin first, more services later

Date: 2026-11-04
Status: approved design

## Goal

Let a visitor send a Meander route straight to their Garmin watch, as a Course in Garmin Connect that appears on the device after a sync. Build the connection layer so later services can be added without changing it. Those services may push routes (Wahoo, Suunto) or pull data (Strava activities).

## Decisions

| Topic | Decision |
|---|---|
| Garmin capability | Push a route as a Course. Do not import activities yet. |
| Garmin access | Use the official Garmin Connect Developer Program Courses API (OAuth 2.0 with PKCE, S256, no scopes). It needs an approved developer application. |
| Before approval | Offer a FIT course download that users load through the Garmin Connect app or over USB. |
| Identity | Use an anonymous per-browser session cookie. There are no accounts. Clearing cookies means reconnecting. |
| Architecture | Keep connections (OAuth and tokens) separate from capabilities. Build `IRouteDestination` now and add `IActivitySource` later. |
| Rejected | A Nango broker, because it adds a container and a dependency for roughly 300 lines of OAuth. A Garmin-only shortcut, because it would need refactoring for the second service. Unofficial Connect login, because it is fragile and grey under the ToS. |

External facts that shaped this:
- Strava's API cannot create routes, only read them.
- Komoot only has a closed partner API.
- Whether Wahoo allows pushing routes is unclear.

So "connect" and "what a service can do" have to be independent.

## Core (`src/Meander.Core/Connections.cs`)

```csharp
public enum ServiceKind { Garmin }
public sealed record ServiceTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? ExternalUserId);
public sealed record ServiceConnection(Guid SessionId, ServiceKind Service, ServiceTokens Tokens, DateTimeOffset ConnectedAt);

public interface IConnectionStore
{
    ServiceConnection? Get(Guid sessionId, ServiceKind service);
    IReadOnlyList<ServiceConnection> List(Guid sessionId);
    void Upsert(ServiceConnection connection);
    void Delete(Guid sessionId, ServiceKind service);
    int DeleteByExternalUser(ServiceKind service, string externalUserId);
}

// OAuth only; no feature logic.
public interface IServiceConnector
{
    ServiceKind Service { get; }
    bool IsConfigured { get; }
    Uri BuildAuthorizeUrl(string state, string codeChallenge, Uri redirectUri);
    Task<ServiceTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct);
    Task<ServiceTokens> RefreshAsync(ServiceTokens tokens, CancellationToken ct);
    Task RevokeAsync(ServiceTokens tokens, CancellationToken ct);
}

// Capability: push a route.
public interface IRouteDestination
{
    ServiceKind Service { get; }
    Task<SentRoute> SendAsync(RouteCandidate route, Activity activity, ServiceTokens tokens, CancellationToken ct);
}
public sealed record SentRoute(string ExternalId, Uri? ViewUrl);
```

**`ConnectionService`** returns valid tokens for a session and service:
- It refreshes when the access token expires within 5 minutes, then saves the new tokens.
- When the refresh is rejected (`invalid_grant`), it deletes the connection and throws `ProviderException(ProviderFailure.Unauthorized)`.
- When there is no connection, it also throws `Unauthorized`.

`ProviderFailure` gains `Unauthorized`.

A later `IActivitySource` takes the same `ServiceTokens` from `ConnectionService`. The connection layer does not change.

## Infrastructure

**`SqliteConnectionStore`**
- Table: `connections (session_id TEXT, service TEXT, external_user_id TEXT, json TEXT NOT NULL, PRIMARY KEY (session_id, service))`.
- It uses the same database file, connection style and lock as `SqliteRouteSearchStore`, but performs real upserts and deletes.
- `json` holds the serialized `ServiceConnection`, encrypted with ASP.NET Data Protection (`IDataProtector` with purpose `Meander.Connections.v1`).
- `external_user_id` holds only a SHA-256 hash of the service's user id, which is enough to look it up for deregistration.
- Rows that cannot be decrypted, for example after the key ring is lost, count as disconnected.
- Keys are persisted to `DataProtection:KeysPath` (default `data/keys`), on the existing appdata volume.

**`OAuthPkce`** produces a code verifier (32 random bytes, base64url), an S256 challenge and a random state.

**Pending-authorization cookie**
- Name: `meander_oauth`, protected with Data Protection.
- Payload: `{ state, verifier, service, returnUrl, expiresAt }`.
- Lifetime: 10 minutes. It is HttpOnly and SameSite=Lax.

**`GarminOptions`** (section `Integrations:Garmin`)
- Fields: `ClientId`, `ClientSecret`, `AuthorizeUrl`, `TokenUrl`, `ApiBaseUrl`.
- `IsConfigured` is false when `ClientId` is empty. The service is then not listed, and the FIT download still works.
- When `ClientId` is set, startup validation requires absolute HTTPS URLs and a secret.

**`GarminConnector : IServiceConnector`**
- Token exchange, refresh and revoke go to the Garmin OAuth2 endpoints.
- Garmin's user id is resolved after the token exchange and stored as `ExternalUserId`, for deregistration.

**`GarminCourseDestination : IRouteDestination`**
- It uses a typed HttpClient with a 20-second timeout.
- No Garmin call is retried automatically: authorization codes are single-use, and a retried course POST could create a duplicate course.

**`GarminCourseMapper`** maps a `RouteCandidate` to the Course payload:
- name `Meander {distance} km`
- distance in metres
- elevation gain
- `activityType` RUNNING or WALKING
- WGS84 geo points with elevation

This is the only place that knows Garmin's payload shape. Verify it against the Garmin docs once access is granted.

**`FitCourseWriter`** sits next to `GpxWriter` and uses the official `Garmin.FIT.Sdk` package. It writes these FIT messages:
- `FileId` (type Course, manufacturer Development)
- `Course` (name, sport Running or Walking)
- `Event` timer start
- a `Record` per point: lat/lon in semicircles, altitude, cumulative distance, and a synthetic timestamp at a 6 min/km pace
- `Lap` (start and end position, total distance)
- `Event` timer stop

## API (`src/Meander.Api/ConnectionEndpoints.cs`, mapped from `Program.cs`)

| Endpoint | Behaviour |
|---|---|
| `GET /api/connections` | Returns `[{ service, connected }]` for every configured service in this session. |
| `GET /api/connections/{service}/authorize?returnUrl=` | Ensures a session cookie, creates PKCE and state, sets the pending cookie, and returns 302 to the provider. Returns 404 for an unknown or unconfigured service. |
| `GET /api/connections/{service}/callback?code=&state=` | Checks the pending cookie (state, service, expiry), exchanges the code, upserts the connection, clears the pending cookie, and returns 302 to `returnUrl` with `connected={service}`. A mismatch returns 400. Provider errors (`error=access_denied`) return 302 with `connectError={service}`. |
| `DELETE /api/connections/{service}` | Revokes the tokens (best effort, failures are logged) and deletes the connection. Returns 204. |
| `POST /api/routes/{id}/send/{service}` | Loads the route from `ICandidateStore`, falling back to `IRouteSearchStore.GetCandidate`. It gets valid tokens and calls the service's `IRouteDestination`. Returns 200 `SentRoute`, 401 when not connected or expired, and 404 for an unknown route or service. |
| `GET /api/routes/{id}/fit` | Returns a FIT course file. It uses the same lookup as `send`, and returns 404 with "export expired" when the route is gone. |
| `POST /api/webhooks/garmin/deregistration` | Garmin's deregistration notification. Deletes connections by Garmin user id and always returns 200. |

- **Session cookie** `meander_sid`: a random GUID that is HttpOnly, SameSite=Lax, Secure outside Development, and lasts 1 year. It is only issued when a visitor starts connecting.
- **`returnUrl`**: must be a local path, starting with `/` but not `//`. Anything else falls back to `/`.
- **Redirect URI**: `{PublicBaseUrl}/api/connections/{service}/callback`. `PublicBaseUrl` is configured, never taken from request headers.
- **Rate limit**: a fixed-window policy `connect` per client IP covers authorize, callback and send.
- **Error mapping**: `ProviderException(Unauthorized)` maps to 401 "Connection expired". Other provider errors on connection endpoints use service-neutral titles.

## Frontend

**`api.ts`**: `listConnections`, `connectUrl`, `disconnect`, `sendRoute` and `downloadFit`. `downloadFit` follows the `downloadGpx` pattern.

**Route card**: the GPX button becomes an export group containing:
- Download GPX
- Download FIT
- "Send to Garmin" for each configured service

Behaviour of the send button:
- When the visitor is not connected, it navigates to `connectUrl(service, currentRouteUrl)`.
- On return with `?connected=garmin&route=…`, the app sends that route automatically, shows a toast, and strips the query parameter.
- A 401 response clears the connected state and offers to reconnect.

**`ConnectionsPanel.tsx`** lists each service with its status and a Disconnect action. It lives in its own component so `App.tsx` does not grow.

## Configuration and deployment

- `appsettings.json`:
  - adds `Integrations:Garmin` (empty means off), `PublicBaseUrl` and `DataProtection:KeysPath`.
  - default Garmin OAuth URLs: authorize `https://connect.garmin.com/oauth2Confirm`, token `https://connectapi.garmin.com/di-oauth2-service/oauth/token`. Confirm both against Garmin's OAuth2 PKCE spec once access is granted.
- `deploy/unraid/meander/docker-compose.yml`:
  - adds `PublicBaseUrl=https://meander.roesink.dev`.
  - reads `Integrations__Garmin__ClientId` and `Integrations__Garmin__ClientSecret` from a server-side `.env`, which is not in git.
- `README.md` gets an "Integrations" section covering:
  - registering the Garmin app
  - the redirect and deregistration URLs
  - adding a new service (connector plus capability)

## Testing

**Backend (xUnit)**
- `FitCourseWriter`: decode with the FIT SDK and check the file type, record count, coordinates and distance.
- `SqliteConnectionStore`: round trip, upsert, delete by external user, and that the stored json contains no plain token.
- `ConnectionService`: no refresh when the token is valid, refresh near expiry, and `invalid_grant` deletes the connection and throws.
- API tests through `ApiFactory`, using a fake connector and destination for the generic flow and a `StubGarmin` handler for Garmin:
  - authorize leads to callback
  - state mismatch returns 400
  - a hostile `returnUrl` is replaced
  - send returns 200, and 401 when not connected
  - disconnect works
  - the deregistration webhook works
- `GarminCourseMapper` payload test.

**Frontend (Vitest)**
- the export group
- the connect redirect
- auto-send after `?connected=`
- the 401 and reconnect path

**End-to-end (Playwright)**
- `e2e/upstream.mjs` gains fake Garmin authorize, token and course endpoints.
- The test runs the whole flow.

## Out of scope

- Importing activities.
- Strava, Wahoo and other services.
- User accounts.
- Pushing workouts.
- Syncing course edits back from Garmin.
