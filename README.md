# Meander

A local-first running and walking route planner. Choose a start, optionally an end and radius, and a target distance. Compare up to three distinct routes on a MapLibre map and download GPX files.

## Run locally

Prerequisites: .NET 10 SDK, Node.js 26+, and ORS and MapTiler accounts. Docker Desktop with Linux containers is optional. To run without accounts or keys, see [Run without API keys](#run-without-api-keys).

1. Get an [OpenRouteService API key](https://openrouteservice.org/) and a [MapTiler browser key](https://cloud.maptiler.com/). Enable `localhost` in the MapTiler key's allowed HTTP origins for local development.
2. Copy `frontend/.env.example` to `frontend/.env.local` and replace `VITE_MAPTILER_KEY`. This key is browser-visible by design. Never put the ORS key in a `VITE_` variable.
3. Start the API from the project root (PowerShell):

   ```powershell
   $env:Ors__ApiKey = 'YOUR_ORS_KEY'
   $env:ASPNETCORE_ENVIRONMENT = 'Development'
   dotnet run --project src/Meander.Api --no-launch-profile --urls http://127.0.0.1:5080
   ```

   On bash, prefix the command with `Ors__ApiKey=YOUR_ORS_KEY ASPNETCORE_ENVIRONMENT=Development` instead. An ignored `src/Meander.Api/appsettings.Local.json` can also hold local settings:

   ```json
   { "Ors": { "ApiKey": "YOUR_ORS_KEY" } }
   ```

4. In a second terminal:

   ```powershell
   cd frontend
   npm ci
   npm run dev
   ```

Open **http://127.0.0.1:5173**. Vite proxies `/api` to port 5080; no CORS configuration is needed. Health: `http://127.0.0.1:5080/api/health`. Development OpenAPI: `http://127.0.0.1:5080/openapi/v1.json`.

Without a MapTiler key or local PMTiles basemap, an actionable message appears and place search still works. The API refuses to start without an ORS key when `Ors:BaseUrl` points to the hosted `api.openrouteservice.org`. Live generation needs a working ORS account and remaining quota.

## Docker Compose

Copy `.env.example` to `.env`, set both keys, then run from the root:

```powershell
docker compose up --build
```

Open **http://127.0.0.1:5173**. The API is on **http://127.0.0.1:5080**. Both ports bind only to localhost. Frontend and backend source changes are watched; container-specific dependencies and build outputs use separate volumes. After dependency changes, run `docker compose up --build` and allow the development watchers to restore updated dependencies. Saved searches live in the named `route-data` volume; preserve it when refreshing other development volumes.

Stop with `docker compose down`. Saved routes survive stops, restarts, and container rebuilds. `docker compose down --volumes` deletes them. The default development setup uses hosted ORS and an embedded SQLite database; there is no separate database service or downloaded routing graph. `compose.local.yaml` adds local routing, geocoding and map data instead (see below).

Build the production-shaped single-container image separately:

```powershell
docker build --build-arg VITE_MAPTILER_KEY=YOUR_BROWSER_KEY -t meander:local .
docker run --rm -p 127.0.0.1:8080:8080 -v meander-routes:/app/data -e Ors__ApiKey=YOUR_ORS_KEY meander:local
```

The image serves the built frontend and API on one origin, runs as a non-root user, and contains no ORS key. Open `http://127.0.0.1:8080`. MapTiler configuration is applied at frontend build time. Azure provisioning and deployment are intentionally deferred.

## Run without API keys

Both keys are optional replacements for hosted services. Keyless mode uses three local services for the Netherlands:

| Hosted | Local replacement | What changes |
| --- | --- | --- |
| ORS routing and snapping | Self-hosted [openrouteservice](https://giscience.github.io/openrouteservice/) container (`foot-walking` only) | `Ors:BaseUrl` points to the container; no key or `Authorization` header |
| ORS geocoding (autocomplete) | [Photon](https://github.com/komoot/photon) container with the Netherlands index | `Geocoding:Provider=Photon`; self-hosted ORS has no geocoder |
| MapTiler basemap | [Protomaps](https://protomaps.com/) PMTiles extract, glyphs and sprites served by the API at `/map` | `MapData:Path` on the API, `VITE_PMTILES_URL` in the frontend |
| Public Overpass (woodland lookup, keyless but often busy) | [Overpass API](https://github.com/wiktorn/Overpass-API) container with a Netherlands import | `Woodland:Url` points to the container |

Needs roughly 6 GB RAM for the containers (ORS uses up to 4 GB while building) and about 21 GB disk (1.3 GB OSM extract, ~2 GB PMTiles, ~4 GB Photon index, ~9 GB Overpass database plus graphs and elevation cache; the Photon and Overpass imports need extra temporary space). The Overpass import took about 45 minutes on a desktop machine.

1. Download the data into the ignored `data/` folder (Docker required for the `pmtiles` CLI):

   ```powershell
   ./scripts/fetch-map-data.ps1     # or: sh scripts/fetch-map-data.sh
   ```

2. Start everything with the local override (no keys needed in `.env`):

   ```powershell
   docker compose -f compose.yaml -f compose.local.yaml up --build
   ```

   If you used the Compose setup before this feature, add `--renew-anon-volumes` once: the existing `web` container keeps its old `node_modules` volume, which lacks the `pmtiles` packages.

   On first start, ORS builds its routing graph (about 5–20 minutes, depending on the machine), Photon downloads its Netherlands index, and Overpass downloads, converts and imports the Netherlands and then builds its areas (one to several hours). Until Overpass is healthy, green searches return routes with a `woodlandUnavailable` warning. Generation returns 503 until ORS reports healthy (`docker compose ps`). Graphs, elevation data and the Photon index persist in named volumes. After downloading a newer OSM extract, remove the `ors-graphs` volume so the graph is rebuilt.

Self-hosted ORS has no green index, so the override sets `Ors:SupportsGreen=false`. **Prefer green** still works through the Overpass woodland lookup; only ORS's green weighting for approaches and the legacy `greenScore` are unavailable. The override raises the ORS request budgets because a local instance has no quota.

Running natively instead of with Compose: start the ORS and Photon containers yourself, then set `Ors__BaseUrl`, `Ors__SupportsGreen=false`, `Geocoding__Provider=Photon`, `Photon__BaseUrl` and `MapData__Path` (e.g. the absolute path of `data/map`) for the API, and `VITE_PMTILES_URL=/map/netherlands.pmtiles` in `frontend/.env.local`. Vite proxies `/map` to the API. For the production image, build with `--build-arg VITE_PMTILES_URL=/map/netherlands.pmtiles` and mount `data/map` at the `MapData__Path` location.

The options are independent: for example, keep hosted ORS with a key and replace only MapTiler with the local basemap.

## Configuration

Backend defaults live in `src/Meander.Api/appsettings.json`; ignored `appsettings.Local.json` overrides them, and environment variables override both. Use double underscores for environment nesting, e.g. `Generation__DeadlineSeconds=60`.

| Setting | Default | Purpose |
| --- | --- | --- |
| `Ors:ApiKey` | required for hosted ORS | Server-side ORS credential; omitted for a self-hosted `Ors:BaseUrl` |
| `Ors:BaseUrl` | `https://api.openrouteservice.org/` | Include the `/ors/` prefix when required by a self-hosted instance |
| `Ors:SupportsGreen` | true | Set false for ORS instances without a green index; green extras and weighting are then not requested |
| `Geocoding:Provider` | `Ors` | `Ors` (hosted geocoding) or `Photon` |
| `Photon:BaseUrl` | `http://localhost:2322/` | Photon instance used when `Geocoding:Provider` is `Photon` |
| `Photon:Language` | empty | Optional Photon `lang` parameter; empty uses local names |
| `MapData:Path` | empty | Directory served at `/map` (PMTiles, glyphs, sprites); must exist when set |
| `Ors:Concurrency` | 3 | Process-wide concurrent HTTP attempts |
| `Ors:AttemptTimeoutSeconds` | 10 | Timeout for each HTTP attempt |
| `Ors:RetryCount` | 2 | Retries after transient failures; 0 disables retries |
| `Ors:DirectionsPerMinute` | 35 | Sliding-window outbound directions budget, including retries |
| `Ors:SnapsPerMinute` | 90 | Separate snapping budget |
| `Ors:GeocodingPerMinute` | 90 | Separate autocomplete budget |
| `Generation:MaxDistanceKm` | 60 | Accepted target range starts at 0.5 km; configured maximum may not exceed 100 km |
| `Generation:SnapRadiusM` | 1000 | Endpoint snap search radius |
| `Generation:SnapWarningM` | 200 | Warn when the start or end moves farther than this |
| `Generation:DistanceTolerance` | 0.05 | Relative distance tolerance |
| `Generation:DeadlineSeconds` | 45 | Overall generation deadline, including snapping and provider search |
| `Generation:CacheTtlMinutes` | 30 | Unsaved drawn-preview lifetime in memory |
| `ResultStorage:DatabasePath` | `data/meander.sqlite` | SQLite file, relative to the API content root; Compose uses `/app/data/meander.sqlite` |
| `Woodland:Url` | `https://overpass-api.de/api/interpreter` | OSM woodland/path lookup; replace with a hosted or local Overpass endpoint |
| `Woodland:MaxSearchRadiusKm` | 12 | Maximum woodland search radius; cached queries include a 1 km centre-rounding margin |
| `Woodland:TimeoutSeconds` | 10 | Time budget for optional woodland lookup, including queued requests |
| `Woodland:MaxResponseMegabytes` | 8 | Woodland response cap (query memory and element limits scale with it); `compose.local.yaml` uses 32 because dense towns exceed 8 MB at large radii |
| `Woodland:CacheHours` | 12 | Lifetime of successful area lookups in a separate 32 MiB memory cache |
| `RateLimiting:GeneratePerMinute` | 3 | Per-IP incoming generation limit |
| `RateLimiting:ConcurrentGenerations` | 2 | Process-wide generation limit; rejected requests are not queued |
| `RateLimiting:GeocodePerMinute` | 60 | Per-IP incoming autocomplete limit |
| `RateLimiting:PreviewPerMinute` | 30 | Per-IP incoming drawn-section previews |
| `AllowedHosts` | `localhost;127.0.0.1` | Accepted hostnames; Compose also allows its internal `api` hostname |

Provider HTTP calls have a 30-second total timeout across retries. Generation has its own overall deadline. `Retry-After` is honored within those deadlines. A local outbound-budget rejection does not itself consume retries. The configured provider URL supports HTTPS and HTTP, including internal HTTP addresses for a future self-hosted routing container.

Frontend configuration (`frontend/.env.local`, restart Vite after edits):

| Variable | Purpose |
| --- | --- |
| `VITE_MAPTILER_KEY` | Public browser key; restrict allowed origins |
| `VITE_PMTILES_URL` | Keyless local basemap, e.g. `/map/netherlands.pmtiles`; takes precedence over MapTiler |
| `VITE_MAP_ASSETS_URL` | Glyphs and sprites for the local basemap; defaults to `/map/assets` |
| `VITE_MAP_STYLE_URL` | Optional OSM-based MapLibre style URL instead of MapTiler's quiet Base v4 style |
| `VITE_MAP_STYLE_DARK_URL` | Optional dark counterpart for a custom map style; default MapTiler maps switch to Base v4 Dark automatically |
| `VITE_MAX_DISTANCE_KM` | Slider maximum; default 60, keep aligned with the backend maximum |
| `API_PROXY_TARGET` | Development API address; defaults to `http://127.0.0.1:5080` |

Appearance follows the system setting by default. The header's system/sun/moon buttons allow an override, which is stored in the browser. Dark mode uses charcoal surfaces, near-white text and orange accents; light mode retains the green palette. Basemap changes retain the current camera, route overlays, selection and markers. Activity and scenery controls use inline SVG icons; decorative slogans and empty-state filler have been removed.

Provider limits vary by account. Check your dashboard and [ORS restrictions](https://openrouteservice.org/restrictions/) before increasing budgets. A loop uses ten directions calculations plus one snap call; A→B uses at most nine directions calls plus snapping, before retries. Autocomplete uses a 350 ms debounce after three characters. API authorization headers are never returned to the browser, and HTTP-client URL logging is disabled at the normal log level.

## API

`POST /api/routes/generate`, `Content-Type: application/json`:

```json
{
  "start": { "latitude": 52.0907, "longitude": 5.1214 },
  "end": null,
  "radiusKm": 2,
  "targetDistanceKm": 5,
  "activity": "running",
  "candidateCount": 3,
  "preferGreen": true,
  "preferUnpaved": true,
  "avoidBacktracking": true,
  "distanceFlexibilityPercent": 15
}
```

`end` and `radiusKm` are optional. Identical endpoint coordinates make a loop. Activities are `running` and `walking`; both currently use ORS `foot-walking`. `candidateCount` accepts 1–3 and defaults to 3. Request coordinates use named properties; GeoJSON output coordinates use **longitude, latitude, optional elevation**.

The nonempty array response contains `id`, `searchId`, `expiresAt` (null for saved routes), GeoJSON `geometry`, `distanceKm`, nullable `elevationGainM`, `maxDistanceFromStartKm`, `score` (lower is better), endpoint snap information, `status`, and `{ code, message }` warnings. Optional quality fields are `woodlandPercent` (approximate share inside mapped woodland), `greenScore` (legacy ORS index, not displayed), `unpavedPercent` (confirmed share of total route length), and `surfaceKnownPercent`.

### Saved searches

Every successful generation is saved automatically in SQLite, together with its input settings and any drawn section. The browser opens `/routes/{searchId}?route={candidateId}`. Bookmark or copy that URL to reopen the same results and selection. Refresh, direct navigation and browser back/forward load stored geometries without calling ORS. GPX exports for saved routes have no TTL. Each generated search gets its own immutable snapshot, so later searches do not overwrite earlier URLs.

The map switches to browsing after picking a point or generating routes. Clicking beside a route leaves the results intact. To change a location, explicitly choose **Pick start on map**, **Pick end on map**, or draw mode.

SQLite files are ignored by Git and Docker build contexts. Native development keeps the file under `src/Meander.Api/data`; Compose keeps it in the persistent `route-data` volume. Back up that data to retain saved links. Localhost URLs work on the computer hosting the app. Links have no account checks; anyone who can reach the app and knows the URL can view that saved search.

`preferGreen` and `preferUnpaved` default to false. Both keep the accepted distance range and radius status ahead of scenery in selection. Green routing now means actively seeking woodland: an `IWoodlandProvider` looks up OSM `natural=wood` / `landuse=forest` polygons and public walking-path samples nearby. The generator clusters adjacent woodland paths, selects up to three promising areas, and places routing waypoints on those paths. Approaches and returns are still routed by ORS. Four initial forest circuits and four adjusted circuits leave two general round trips as fallback, within the existing ten-call budget. For A→B, the shortest baseline remains first, followed by six forest attempts and two general detours (nine calls maximum). Fixed drawn prefixes are preserved.

The visible **mapped woodland** percentage is measured on the returned geometry using length-weighted samples at at most 25 m intervals against actual woodland boundaries, including multipolygon holes. Streets with trees, lawns, fields and generic green map colours do not automatically count as woodland. This is an estimate of mapped woodland, not canopy density, legal access, or a satellite measurement. Missing OSM mapping and the capped lookup extent can undercount woodland. The former ORS green index includes broader greenery and is no longer displayed. It remains in saved/API data for compatibility and ORS's green routing weighting still helps route the approaches. Old saved searches remain immutable and need regeneration to gain woodland metrics and the new search behavior.

With woodland data and both preferences selected, woodland receives 70% and unpaved receives 30% of the scenery penalty. Unpaved routing uses confirmed gravel, dirt, ground, sand, grass, and other natural surfaces; it is a preference, not a requirement that every segment be unpaved. Unknown surfaces remain unknown. See [ORS weightings](https://giscience.github.io/openrouteservice/api-reference/endpoints/directions/routing-options) and [surface IDs](https://giscience.github.io/openrouteservice/api-reference/endpoints/directions/extra-info/surface).

Woodland lookup needs no additional API key. The default public Overpass endpoint is suitable for occasional local use and can be busy. Queries are coalesced by rounded centre and radius, globally serialized with at least one second between starts, cached for 12 hours, and bounded to 8 MiB responses. Failed lookups are cached for two minutes without immediate retries. A timeout, 429, incomplete response or unavailable service returns normal routing alternatives with a clear `woodlandUnavailable` warning and no invented woodland percentage. No woodland reached after a successful lookup produces `woodlandNotReached`. Follow the [Overpass usage policy](https://dev.overpass-api.de/overpass-doc/en/preface/commons.html); configure a self-hosted or dedicated endpoint for production. Loading a saved search never performs a new woodland lookup.

`distanceFlexibilityPercent` defaults to 0 (normal ±5% distance matching). Set 5–30 to allow a wider band and give selected scenery preferences more weight within that band. The UI's Flexible distance switch starts at ±15%, offers margins of 10–30%, and displays the actual permitted kilometre range, capped at 0.5 km and the configured maximum. Flexible generation explores shorter and longer routes within the existing provider-call budget. A point-to-point request can use a shortest route longer than the nominal target when it still falls inside the permitted band. Radius diagnostics continue to rank behind routes inside the radius.

The interface always sends `avoidBacktracking: true`; its checkbox has been removed. The API retains the field for compatibility. Immediately reversed path segments are removed before scoring, radius checking, and caching. The same cleaned geometry is displayed and exported. Distance is scaled by the retained geometry length and elevation is recalculated from retained coordinates (or becomes unavailable when elevations are missing). Genuine circuits, nearby parallel paths, and shared start/end access stems are preserved. If only a pure out-and-back route is possible, try a different start. Explicit manual drawing points remain intentional and are preserved.

### Draw a fixed section with click points

Choose **Draw a route section**, then click points along the paths you want to take. If no start is selected, the first click sets it. Up to 20 subsequent points can be added. Meander connects them through the routing network and shows a preview, numbered snapped points, and the drawn distance. **Undo**, **Clear drawing**, and **Refresh drawing** let you revise it. Preview requests are debounced by 500 ms and obsolete requests are cancelled.

Drag a numbered marker to move that point; the start/end markers can also be dragged. The preview is recalculated once the marker is released. Select a numbered marker and press **Delete** to remove it. **Undo**, **Ctrl+Z** (or **Cmd+Z**) reverses the latest addition, removal, marker move or clearing of the drawing. Shortcuts leave native editing intact while typing in text or numeric fields. Marker clicks and drag release never add an extra route point.

### Edit a suggested route

Choose **Edit route** on a result card. The editor copies the exact stored geometry and introduces up to 19 intermediate control points, spaced approximately every 600 m (with wider spacing for longer routes). Opening an editor does not call ORS or change the found route. Drag points to reshape it. Click a numbered marker to reveal **Remove point** and **Insert after**. Clicking the map otherwise inserts a point into the nearest part of the actual routed geometry. Every subsequent change reconnects the controls through the walking network, so paths between controls can change. Loops keep their return to the start; point-to-point routes keep a separate end marker.

Use **Save changes** or **Save edited route** to save the exact current preview as a new search URL, without running another candidate search. The original search URL stays available, and **Cancel editing** restores it. **Download edited GPX** exports the current preview. Unsaved editor drafts have the normal 30-minute TTL; saved edits and their control points persist in SQLite and restore without routing calls. Undo restores a cached earlier preview when possible, including the exact initial found geometry.

While recalculating, or if a preview request fails, the last valid route remains visible as a faded dashed line. Moved markers retain their new positions. Saving and exporting that pending edit stay unavailable until its preview succeeds; **Undo** can restore the last valid state. ORS snap responses may omit `snapped_distance` for valid points; the adapter then calculates it from the original and snapped coordinates.

Snap distances are measured from coordinates rather than trusting optional provider metadata. Invalid optional surface/green spans are discarded with `qualityDataUnavailable`, while the valid route geometry remains usable; unknown elevation stays null. Mandatory geometry, distance and snapping cardinality are still validated. A preview retries a malformed provider response once after 200 ms, within its existing deadline; no-route responses are not retried. Structural parser failures include `providerStage` (`snap` or `directions`) in ProblemDetails, and server logs record the parsing exception with the trace ID, without logging request headers or API keys.

- `POST /api/routes/{id}/edit` returns `{ preview, request, points, isLoop }` and caches an editable copy. It reads a saved route; it does not call ORS.
- `POST /api/routes/save` accepts a `RouteRequest` with `drawnRouteId`, checks the draft's endpoints/activity/preferences and saves it as one candidate. It preserves current distance/radius status and warnings and does not call ORS. The saved request has `isEditedRoute: true`; the last stored drawing point is the finish, represented by A/B rather than a duplicate numbered marker in the editor.

Use **Download drawn GPX** to export that section directly (click back at your start if you are drawing a complete loop). Or set a target distance and choose **Find my routes** to keep the exact preview geometry as a fixed prefix and automatically complete the rest to your selected end, or back to the start. Explicitly clicked out-and-back legs are preserved; automatic cleanup applies only to the generated continuation. This is also how to steer a route into a particular forest: the green background map by itself does not select a search area.

With **Prefer green** or **Prefer unpaved**, the last drawn point (when it is not back at the start) marks the area to explore. If there is at least 0.6 km of distance to spare beyond the shortest way home, most of the search budget goes there: forest circuits through mapped woodland whose paths are within a third of the spare distance (at least 1 km) of that point, or, without nearby woodland data, loops beyond the point on the side away from home. Two general detours remain as alternatives. Continuations that stay near the drawn point rank ahead of similar detours elsewhere. A forest that is closer to home than to the drawn point is treated as part of the way back, not as the target.

`POST /api/routes/preview` accepts `{ start, points: [{ latitude, longitude }, ...], activity, preferGreen, preferUnpaved }` and returns a cached `RouteCandidate` with `snappedPoints`. Its ID may be sent as `drawnRouteId` on the normal generate request. The fixed route must match the requested start, activity, and scenery preferences. The cache stores its complete per-edge quality data so completion can preserve geometry and calculate whole-route distance and quality. Expired or mismatched drafts return 422 with an instruction to refresh. The minimum possible completion distance includes the entire fixed prefix plus the shortest connection. A drawn point outside the requested radius is rejected.

Each preview uses one batched snap and one directions request. Generating from a cached drawing reuses the fixed section without recalculating it. Unsaved drafts have a 30-minute TTL in bounded memory and disappear on API restart. Completing a search saves its draft, including per-edge quality data, permanently with the search. Reopening that search restores the drawing without new provider calls, and its prefix can be reused after a restart.

| Status | Meaning |
| --- | --- |
| `qualified` | Inside the radius, if supplied, and within the accepted distance range (normally ±5%, or the selected flexible margin) |
| `bestEffort` | Inside the radius but outside target-distance tolerance |
| `diagnostic` | Exceeds the requested radius; explicitly labeled and drawn dashed |

Qualifying routes rank first. If fewer than three remain, distance misses and then diagnostic routes fill available slots; duplicate geometries are never used to fill the count. Diagnostics may be exported, with their warnings included in GPX. Radius always uses the **original requested start**, even if snapping moves the endpoint. A supplied end outside that radius remains an input error.

- `GET /api/geocode?q=Utrecht` returns up to five `{ id, label, location }` suggestions. Queries must be 3–200 characters.
- `GET /api/searches/{searchId}` returns the saved request, candidates, requested drawing points and drawn preview, or ProblemDetails 404. It does not call ORS.
- `GET /api/routes/{id}/gpx` returns GPX 1.1 with optional elevation, warnings, attribution, and attachment headers. Export does not call ORS.
- Errors use `application/problem+json`: 400 malformed input, 422 invalid constraints/no routable path, 429 application limiting, 502 malformed upstream data, 503 upstream unavailable/throttled, 504 timeout. A target shorter than the measured shortest A→B route includes `minimumDistanceKm`. Error responses include a trace ID.
- No usable route results in 422 or an upstream failure status, never an empty 200. Independent call failures retain usable candidates with `partialSearch` warnings.

## Algorithm and limitations

Loops vary seeds and round-trip point counts. Half the round trips run first; if the closest cleaned route misses the distance tolerance, the remaining half adjusts the provider target length to compensate, within the same ten-call budget. A radius search combines six ORS round trips and four explicit closed waypoint routes whose generated points stay inside 90% of the radius. A→B first requests the shortest route and then tries two mirrored, alternating-side detour families, adjusting offsets up to four times per family. Path lengths can change non-monotonically, so the search retains all usable observations.

Full returned geometries are checked, including geodesic segment samples at at most 20 m intervals. Normal scoring weights relative distance error at 80% and estimated retracing at 20%, plus a scenery penalty up to 0.25. For flexible, qualifying routes with woodland data, weights become 5% distance error, 10% retracing, 10% distance until first woodland entry, and 75% scenery. Without woodland data the existing flexible weights remain 15% distance, 15% retracing, and 70% scenery. Qualified routes still rank ahead of distance misses, followed by radius diagnostics. Direction-independent, length-weighted proximity within 20 m estimates retracing and overlap; more than 70% overlap of the shorter geometry removes a duplicate. Adjacent segments are excluded from the retracing check. These are geometric approximations, so nearby parallel paths can count as overlapping. Spur cleanup uses a separate, conservative 1 m path-alignment tolerance; it does not remove loops merely because they revisit a junction.

A small radius alone does not prove a target distance impossible. Meander attempts a bounded search rather than rejecting winding routes using a circumference heuristic. Islands, water, sparse networks, and snapped intermediate points can prevent a close match or force a route outside the circle. This is a heuristic generator, not a guarantee of three qualifying routes. Elevation preferences, accounts, PWA installation, and turn-by-turn navigation are deferred.

The transient candidate/drawn-section cache is bounded to approximately 64 MiB per API process. Persisted searches, their GPX exports and saved prefixes fall back to SQLite after cache eviction or restart. Saved searches currently have no automatic deletion or disk quota. For Azure Container Apps, mount durable storage and use a single API replica with this embedded database; multi-replica production hosting needs a shared database implementation of `IRouteSearchStore`. Request budgets and rate limits are process-local.

## Structure and extension

- `src/Meander.Core`: provider-neutral contracts, validation, geodesic geometry, route generation, scoring, and selection. No ASP.NET or ORS dependencies.
- `src/Meander.Infrastructure`: ORS routing/geocoding adapters, profile registry, shared outbound budgets, bounded preview cache, SQLite saved searches, and GPX serialization.
- `src/Meander.Api`: minimal endpoints, ProblemDetails, HTTP resilience, configuration, rate limiting, and production static hosting.
- `frontend`: React hooks/components, Vite proxy, MapLibre rendering with a separately bundled ESM worker, Vitest, and Playwright fixtures.
- `tests/Meander.Tests`: xUnit domain, geometry, cache, export, budgets, and real-adapter API integration tests.

To add a provider, implement `IRoutingProvider` and register it in the API. It must support snapping, round trips (natively or via its own waypoint synthesis), via-point routing, and shortest routing. Geocoding has its own `IGeocodingProvider` because a self-hosted router need not include a geocoder; `PhotonGeocodingProvider` is the self-hosted option. A self-hosted ORS reuses the current adapter with a changed base URL. GraphHopper needs a new adapter, not a rewrite of generation or frontend code.

To add an activity, extend `Activity`, the provider's profile registry, the frontend activity type/selector, and associated tests. No profile strings belong in the generator or API request contract.

## Checks

```powershell
dotnet test Meander.slnx
dotnet publish src/Meander.Api -c Release
cd frontend
npm ci
npm test
npm run build
npx playwright install chromium
npm run test:e2e
```

The E2E starts a local upstream fixture (5091), the real API (5081), and Vite (5174). Ports must be free. It runs the same happy path on desktop and mobile: select a geocoded start, generate three routes, select a route on a working WebGL map, download and parse GPX, and check for horizontal overflow and uncaught browser errors. No real service keys are used. Screenshots and failure traces are written to ignored `frontend/test-results/`.

Live smoke check after adding real keys: search for a start near a connected walking network, generate a 5 km loop, enable a 2 km radius and regenerate, then choose a different end and request a longer route. Confirm candidate geometry, warnings, and exported coordinates. Try an end outside the radius and a target below the measured shortest route to confirm validation. This live check uses your ORS quota.

Map and routing attribution remains visible. Map data: [OpenStreetMap contributors](https://www.openstreetmap.org/copyright). Routing/geocoding: [openrouteservice](https://openrouteservice.org/). Vector tiles: [MapTiler](https://www.maptiler.com/), or [Protomaps](https://protomaps.com/) in keyless mode. Keyless geocoding: [Photon](https://photon.komoot.io/).
