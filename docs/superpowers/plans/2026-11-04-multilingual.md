# Multilingual Meander Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Meander's UI, route warnings and API errors appear in Dutch or English, defaulting to the browser language, with an NL | EN toggle.

**Architecture:** The frontend owns all user-facing text in two typed dictionaries (`en` is the source, `nl` must match its keys). The API keeps its English text as a fallback but adds stable codes and numeric parameters to warnings and errors, which the frontend translates. UI state stores message descriptors, not finished strings, so switching language re-renders everything already on screen.

**Tech Stack:** .NET 10 minimal API + xUnit; React 19 + TypeScript + Vite, Vitest + Testing Library, Playwright; MapLibre with Protomaps basemaps.

**Spec:** `docs/superpowers/specs/2026-11-04-multilingual-design.md`

## Global Constraints

- Languages: `nl` and `en` only. Anything else falls back to `en`.
- Language resolution order: `localStorage['meander.language']` → first `navigator.languages` entry whose primary subtag is `nl` or `en` → `en`.
- `localStorage` access is always wrapped in try/catch (pattern of `useTheme`).
- No i18n library. `nl` is typed `Record<MessageKey, string>` against `en`, so missing keys fail `tsc`.
- API `message`/`detail` stay English; codes and params are additions, never replacements.
- Coordinates keep a dot decimal separator in both languages. Distances and other numbers use `Intl.NumberFormat` of the current language.
- GPX contents and README stay English.
- Run backend tests with `dotnet test Meander.slnx` from the repo root; frontend with `npm test`, `npx tsc -b` and `npm run test:e2e` from `frontend/`.

## File Structure

Backend:
- Modify `src/Meander.Core/Domain.cs`: `RouteWarning` gets `Params`; `RouteValidationException` gets `Code` and `Params`; `ProviderFailure` code mapping; `IGeocodingProvider.SearchAsync` gets `language`.
- Modify `src/Meander.Core/RouteGenerator.cs`, `RouteEditor.cs`: pass codes/params.
- Modify `src/Meander.Infrastructure/OrsProvider.cs` (ORS warning code param, geocoder signature), `PhotonGeocodingProvider.cs` (language), `OrsOptions.cs` (activity code).
- Modify `src/Meander.Api/Program.cs`: ProblemDetails `code`, geocode `lang`.
- Tests in `tests/Meander.Tests/ApiTests.cs`, `GenerationTests.cs`.

Frontend:
- Create `frontend/src/locales/en.ts`, `frontend/src/locales/nl.ts`: dictionaries.
- Create `frontend/src/i18n.tsx`: language detection, provider, `useI18n()`, `Message` type, number formatting.
- Create `frontend/src/apiText.ts`: warnings and ProblemDetails → `Message`.
- Modify `frontend/src/App.tsx`, `components/LocationSearch.tsx`, `components/RouteMap.tsx`, `mapStyle.ts`, `api.ts`, `types.ts`, `styles.css`.
- Tests: `frontend/src/i18n.test.tsx`, `frontend/src/apiText.test.ts`, additions to `App.test.tsx`, `mapStyle.test.ts`, `api.test.ts`, `e2e/route.spec.ts`.

---

### Task 1: Warning params in the API

**Files:**
- Modify: `src/Meander.Core/Domain.cs:21`
- Modify: `src/Meander.Core/RouteGenerator.cs` (warnings at lines ~31, ~195, ~197, ~385, ~400)
- Modify: `src/Meander.Core/RouteEditor.cs:47`
- Modify: `src/Meander.Infrastructure/OrsProvider.cs:92-94`
- Test: `tests/Meander.Tests/GenerationTests.cs`, `tests/Meander.Tests/OrsResponseTests.cs`

**Interfaces:**
- Produces: `public sealed record RouteWarning(string Code, string Message, IReadOnlyDictionary<string, double>? Params = null);` serialized as `{ code, message, params }` (camelCase, `params` omitted→`null` when absent).
- Param names: `drawnPointSnapped{meters}`, `startSnapped{meters}`, `endSnapped{meters}`, `fewerRoutes{found,requested}`, `radiusExceeded{km,radiusKm}` (generation) or `{edited:1}` (editor), `providerWarning{orsCode}` when ORS sends a numeric code.

- [ ] **Step 1: Write failing tests**

In `tests/Meander.Tests/GenerationTests.cs` add (uses existing `FakeRouting`, `MemoryStore`, `TestClock`, `Fixtures`):

```csharp
[Fact]
public async Task SnappedStartWarningCarriesMetersForTranslation()
{
    var provider = new FakeRouting { Snap = points => points.Select(p => (SnapResult?)new(Geo.Destination(p, 0, .2), 200)).ToArray() };
    var routes = await new RouteGenerator(provider, new MemoryStore(), new(), new TestClock()).GenerateAsync(Fixtures.Loop(5), default);
    var warning = routes[0].Warnings.Single(w => w.Code == "startSnapped");
    Assert.Equal(200, warning.Params!["meters"], precision: 0);
}

[Fact]
public async Task FewerRoutesWarningCarriesCounts()
{
    var provider = new FakeRouting { Round = (q, _) => Task.FromResult(Fixtures.Petal(0, distance: q.DistanceKm)) };
    var routes = await new RouteGenerator(provider, new MemoryStore(), new(), new TestClock()).GenerateAsync(Fixtures.Loop(5), default);
    var warning = routes[0].Warnings.Single(w => w.Code == "fewerRoutes");
    Assert.Equal(routes.Count, warning.Params!["found"]);
    Assert.Equal(3, warning.Params["requested"]);
}
```

In `tests/Meander.Tests/OrsResponseTests.cs` add a test that parses an ORS GeoJSON response whose `properties.warnings` is `[{"code":1,"message":"There may be restrictions on some roads"}]` and asserts the resulting `RouteResult.Warnings` contains `providerWarning` with `Params["orsCode"] == 1`. Copy the arrange/act of the nearest existing ORS directions parse test in that file and only change the `warnings` JSON and the assertions:

```csharp
var warning = Assert.Single(result.Warnings!, w => w.Code == "providerWarning");
Assert.Equal("There may be restrictions on some roads", warning.Message);
Assert.Equal(1, warning.Params!["orsCode"]);
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Meander.slnx --filter "SnappedStartWarningCarriesMeters|FewerRoutesWarningCarriesCounts|orsCode"`
Expected: compile error, `RouteWarning` has no `Params`.

- [ ] **Step 3: Implement**

`src/Meander.Core/Domain.cs:21`:

```csharp
// Message stays English for API clients; Code and Params let the UI translate it.
public sealed record RouteWarning(string Code, string Message, IReadOnlyDictionary<string, double>? Params = null);
```

`RouteGenerator.cs` replacements (keep messages as they are, add the params argument):

```csharp
warnings.Add(new("drawnPointSnapped", $"A clicked point was moved more than {options.SnapWarningM:F0} m to a walkable path. Check the drawn section on the map.",
    new Dictionary<string, double> { ["meters"] = Math.Round(options.SnapWarningM) }));
```
```csharp
warnings.Add(new("startSnapped", $"Start moved {start.SnapDistanceM:F0} m to the nearest routable path.",
    new Dictionary<string, double> { ["meters"] = Math.Round(start.SnapDistanceM) }));
```
```csharp
warnings.Add(new("endSnapped", $"End moved {end.SnapDistanceM:F0} m to the nearest routable path.",
    new Dictionary<string, double> { ["meters"] = Math.Round(end.SnapDistanceM) }));
```
```csharp
warnings.Add(new("fewerRoutes", $"Found {selected.Count} distinct route(s); requested {request.CandidateCount}. Try moving the start or changing the distance.",
    new Dictionary<string, double> { ["found"] = selected.Count, ["requested"] = request.CandidateCount }));
```
```csharp
allWarnings.Add(new("radiusExceeded", $"Diagnostic route: reaches {e.MaxDistance:F2} km from start, beyond the {request.RadiusKm:F2} km radius.",
    new Dictionary<string, double> { ["km"] = Math.Round(e.MaxDistance, 2), ["radiusKm"] = Math.Round(request.RadiusKm!.Value, 2) }));
```

(Keep the exact existing message strings from the file; only append the dictionary argument.)

`RouteEditor.cs:47`:

```csharp
if (outsideRadius) warnings.Add(new("radiusExceeded", "The edited route exceeds your maximum radius.",
    new Dictionary<string, double> { ["edited"] = 1 }));
```

`OrsProvider.cs:92-94`, replace the warnings projection:

```csharp
var warnings = properties.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
    ? w.EnumerateArray().Select(item =>
    {
        var message = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()! : "The provider reported a route warning.";
        // ORS warning codes let the UI translate known provider texts.
        var code = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("code", out var c) && c.TryGetDouble(out var number)
            ? new Dictionary<string, double> { ["orsCode"] = number } : null;
        return new RouteWarning("providerWarning", message, code);
    }).ToList() : [];
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Meander.slnx`
Expected: all pass (existing tests construct `RouteWarning(code, message)`, which still compiles thanks to the optional parameter).

- [ ] **Step 5: Commit**

```bash
git add src/Meander.Core/Domain.cs src/Meander.Core/RouteGenerator.cs src/Meander.Core/RouteEditor.cs src/Meander.Infrastructure/OrsProvider.cs tests/Meander.Tests/GenerationTests.cs tests/Meander.Tests/OrsResponseTests.cs
git commit -m "Send numeric params with shown route warnings"
```

---

### Task 2: Error codes in ProblemDetails

**Files:**
- Modify: `src/Meander.Core/Domain.cs:100-112`
- Modify: every `throw new RouteValidationException(` in `src/Meander.Core/RouteGenerator.cs`, `RouteEditor.cs`, `src/Meander.Infrastructure/OrsOptions.cs:52`
- Modify: `src/Meander.Api/Program.cs` (ApiExceptionHandler at ~283-320, endpoint problems at lines ~85, ~149, ~190, ~200, ~213, ~221, ~224, ~225)
- Test: `tests/Meander.Tests/ApiTests.cs`

**Interfaces:**
- Produces: `RouteValidationException(string code, string message, IDictionary<string,string[]>? errors = null, double? minimumDistanceKm = null, IReadOnlyDictionary<string, double>? parameters = null)` with properties `Code`, `Params`.
- Produces: `public static string ProviderFailures.Code(this ProviderFailure failure)` returning `noRoute | timeout | invalidResponse | unavailable | throttled`.
- ProblemDetails extensions: `code` (string, always present), `params` (object of numbers, when present). Existing `minimumDistanceKm`, `errors`, `traceId` stay.
- Codes used (the frontend dictionary in Task 4 has `errors.<code>` for each): `invalidRequest, editExpired, editMismatch, duplicatePoints, noPathNearPoint, drawnSectionExpired, drawnSectionMismatch, pointOutsideRadius, noPathNearStart, drawnSectionTooLong, targetTooShort, drawnConnectionTooLong, noUsableRoute, activityUnsupported, noRoute, timeout, invalidResponse, unavailable, throttled, malformedRequest, serverError, tooManyRequests, savedSearchNotFound, routeNotFound, exportExpired, searchQueryLength, apiNotFound, mapFileNotFound, notFound, requestFailed`.

- [ ] **Step 1: Write failing tests** in `ApiTests.cs`:

```csharp
[Fact]
public async Task ProblemDetailsCarryStableCodes()
{
    await using var app = new ApiFactory(new());
    using var client = app.CreateClient();
    var expired = await client.PostAsJsonAsync("/api/routes/generate", new { start = new { latitude = 52.09, longitude = 5.12 }, targetDistanceKm = 5, drawnRouteId = Guid.NewGuid() });
    Assert.Equal("drawnSectionExpired", (await expired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    var missing = await client.GetAsync($"/api/searches/{Guid.NewGuid()}");
    Assert.Equal("savedSearchNotFound", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    var shortQuery = await client.GetAsync("/api/geocode?q=ab");
    Assert.Equal("searchQueryLength", (await shortQuery.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    var unknown = await client.GetAsync("/api/nope");
    Assert.Equal("apiNotFound", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
}

[Fact]
public async Task ShortestRouteErrorHasCodeAndMinimumDistance()
{
    await using var app = new ApiFactory(new());
    using var client = app.CreateClient();
    using var response = await client.PostAsJsonAsync("/api/routes/generate", new {
        start = new { latitude = 52.09, longitude = 5.12 }, end = new { latitude = 52.1, longitude = 5.13 }, targetDistanceKm = 1 });
    var error = await response.Content.ReadFromJsonAsync<JsonElement>();
    Assert.Equal("targetTooShort", error.GetProperty("code").GetString());
    Assert.Equal(2, error.GetProperty("minimumDistanceKm").GetDouble());
}
```

Extend the existing `UpstreamFailuresHaveExplicitStatusCodes` theory body with one assertion that the problem has a `code` among `unavailable|timeout|throttled|invalidResponse|noRoute`:

```csharp
var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
Assert.Contains(problem.GetProperty("code").GetString(), new[] { "unavailable", "timeout", "throttled", "invalidResponse", "noRoute" });
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Meander.slnx --filter "ProblemDetailsCarryStableCodes|ShortestRouteErrorHasCode|UpstreamFailuresHaveExplicitStatusCodes"`
Expected: FAIL, property `code` not found.

- [ ] **Step 3: Implement**

`Domain.cs`, replace `RouteValidationException` and add the failure-code extension next to `ProviderException`:

```csharp
public sealed class RouteValidationException(string code, string message,
    IDictionary<string, string[]>? errors = null, double? minimumDistanceKm = null,
    IReadOnlyDictionary<string, double>? parameters = null) : Exception(message)
{
    // Stable code for clients that translate the message.
    public string Code { get; } = code;
    public IDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
    public double? MinimumDistanceKm { get; } = minimumDistanceKm;
    public IReadOnlyDictionary<string, double>? Params { get; } = parameters;
}

public static class ProviderFailures
{
    public static string Code(this ProviderFailure failure) => failure switch
    {
        ProviderFailure.NoRoute => "noRoute",
        ProviderFailure.Timeout => "timeout",
        ProviderFailure.Throttled => "throttled",
        ProviderFailure.InvalidResponse => "invalidResponse",
        _ => "unavailable"
    };
}
```

Add the code as first argument at each throw site (message unchanged):

| File / message start | Code |
|---|---|
| `RouteEditor.cs` "This edited route has expired" | `editExpired` |
| `RouteEditor.cs` "Check the route inputs." | `invalidRequest` |
| `RouteEditor.cs` "The edited route belongs to different settings" | `editMismatch` |
| `RouteGenerator.cs` "Check the drawn route points." | `invalidRequest` |
| `RouteGenerator.cs` "Choose different consecutive route points." | `duplicatePoints` |
| `RouteGenerator.cs` "No walkable path was found near one of the clicked points" | `noPathNearPoint` |
| `RouteGenerator.cs` "Check the route inputs." | `invalidRequest` |
| `RouteGenerator.cs` "The drawn section has expired" | `drawnSectionExpired` |
| `RouteGenerator.cs` "The drawn section belongs to different settings" | `drawnSectionMismatch` |
| `RouteGenerator.cs` "A clicked route point is outside the radius" | `pointOutsideRadius` |
| `RouteGenerator.cs` "No walkable path was found near the selected location" | `noPathNearStart` |
| `RouteGenerator.cs` "The drawn section is already longer than the requested route" | `drawnSectionTooLong` |
| `RouteGenerator.cs` ternary "The target is shorter…" / "The drawn section plus the shortest connection…" | `draft is null ? "targetTooShort" : "drawnConnectionTooLong"` |
| `RouteGenerator.cs` `ThrowNoResults` "No usable route was found…" | `noUsableRoute`, plus `parameters: new Dictionary<string, double> { ["avoidBacktracking"] = avoidBacktracking ? 1 : 0 }` |
| `OrsOptions.cs` "This activity is not supported" | `activityUnsupported` |

Example for the ternary:

```csharp
throw new RouteValidationException(draft is null ? "targetTooShort" : "drawnConnectionTooLong",
    draft is null ? "The target is shorter than the shortest route between these locations."
        : "The drawn section plus the shortest connection is longer than the requested route. Increase the distance or remove a point.",
    minimumDistanceKm: baseline.DistanceKm);
```

Fix any test that constructs `RouteValidationException` directly by adding a code (`grep -rn "new RouteValidationException" tests`).

`Program.cs`, `ApiExceptionHandler`: compute a code beside status/title and write it:

```csharp
var code = exception switch
{
    RouteValidationException validation => validation.Code,
    BadHttpRequestException => "malformedRequest",
    ProviderException provider => provider.Failure.Code(),
    TimeoutRejectedException or OperationCanceledException => "timeout",
    HttpRequestException => "unavailable",
    _ => "serverError"
};
```
and after `problem.Extensions["traceId"] = ...`:
```csharp
problem.Extensions["code"] = code;
if (exception is RouteValidationException { Params: { } parameters }) problem.Extensions["params"] = parameters;
```

Endpoint problems: add `extensions: new Dictionary<string, object?> { ["code"] = "<code>" }` to each `Results.Problem(...)`:

| Line (approx.) | Title | Code |
|---|---|---|
| 85 | Too many requests | `tooManyRequests` |
| 149 | `status == 404 ? "Not found" : "Request failed"` | `status == 404 ? "notFound" : "requestFailed"` |
| 190 | Saved routes not found | `savedSearchNotFound` |
| 200 | Route not found | `routeNotFound` |
| 221 | Route export expired or unavailable | `exportExpired` |
| 224 | API endpoint not found | `apiNotFound` |
| 225 | Map file not found | `mapFileNotFound` |

For the geocode validation at ~213 (`Results.ValidationProblem`), pass `extensions: new Dictionary<string, object?> { ["code"] = "searchQueryLength" }`.

- [ ] **Step 4: Run tests**

Run: `dotnet test Meander.slnx`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "Add stable codes to API problem responses"
```

---

### Task 3: Geocoding language

**Files:**
- Modify: `src/Meander.Core/Domain.cs:67-70` (`IGeocodingProvider`)
- Modify: `src/Meander.Infrastructure/PhotonGeocodingProvider.cs`, `src/Meander.Infrastructure/OrsProvider.cs` (its `SearchAsync`)
- Modify: `src/Meander.Api/Program.cs:209-215` (geocode endpoint)
- Test: `tests/Meander.Tests/ApiTests.cs` (+ `StubOrs` gets a `Queries` bag)

**Interfaces:**
- Produces: `Task<IReadOnlyList<GeocodeSuggestion>> SearchAsync(string query, string? language, CancellationToken cancellationToken);`
- Endpoint: `GET /api/geocode?q=…&lang=nl|en` (anything else ignored).

- [ ] **Step 1: Failing test.** In `StubOrs` add `public readonly ConcurrentBag<string> Queries = [];` and in its handler, next to `Requests.Add(...)`, add `Queries.Add(request.RequestUri!.Query);`. Then:

```csharp
[Fact]
public async Task PhotonUsesTheRequestedLanguageOverTheConfiguredOne()
{
    var upstream = new StubOrs();
    await using var app = new ApiFactory(upstream, new() { ["Geocoding:Provider"] = "Photon", ["Photon:BaseUrl"] = "http://photon:2322/", ["Photon:Language"] = "en" });
    using var client = app.CreateClient();
    await client.GetFromJsonAsync<JsonElement>("/api/geocode?q=Utrecht&lang=nl");
    Assert.Contains("lang=nl", Assert.Single(upstream.Queries));
    await client.GetFromJsonAsync<JsonElement>("/api/geocode?q=Utrecht&lang=xx");
    Assert.Contains(upstream.Queries, q => q.Contains("lang=en"));
}
```

- [ ] **Step 2: Run** `dotnet test Meander.slnx --filter PhotonUsesTheRequestedLanguage` — Expected: FAIL (`lang=en` sent for `lang=nl`).

- [ ] **Step 3: Implement.**

`Domain.cs`:
```csharp
public interface IGeocodingProvider
{
    // Language is a UI language hint (nl or en); providers without language support ignore it.
    Task<IReadOnlyList<GeocodeSuggestion>> SearchAsync(string query, string? language, CancellationToken cancellationToken);
}
```

`PhotonGeocodingProvider.SearchAsync(string query, string? language, CancellationToken ct)`: where the request URL appends `&lang=` from `options.Language`, use `var lang = language ?? options.Language;` and append `&lang={Uri.EscapeDataString(lang)}` when `lang` is not empty.

`OrsProvider.SearchAsync(string query, string? language, CancellationToken ct)`: add the parameter, ignore it.

`Program.cs` geocode endpoint:
```csharp
app.MapGet("/api/geocode", async (string? q, string? lang, IGeocodingProvider geocoder, CancellationToken ct) =>
```
and call `geocoder.SearchAsync(q.Trim(), lang is "nl" or "en" ? lang : null, ct)` (keep the existing trimming/validation code).

Update any test fake implementing `IGeocodingProvider` (`grep -rn IGeocodingProvider tests`).

- [ ] **Step 4: Run** `dotnet test Meander.slnx` — Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "Pass the UI language to place search"
```

---

### Task 4: Frontend i18n core and dictionaries

**Files:**
- Create: `frontend/src/locales/en.ts`, `frontend/src/locales/nl.ts`, `frontend/src/i18n.tsx`
- Test: `frontend/src/i18n.test.tsx`

**Interfaces:**
- Produces from `i18n.tsx`:
  - `export type Language = 'nl' | 'en'`
  - `export type MessageKey = keyof typeof en`
  - `export type PluralBase = 'drawing.points'` (keys that exist as `.one`/`.other` pairs)
  - `export type Params = Record<string, string | number>`
  - `export type Message = { key: MessageKey; params?: Params } | { text: string }`
  - `export function detectLanguage(): Language`
  - `export function I18nProvider({ children }: { children: ReactNode }): JSX.Element`
  - `export function useI18n(): { language: Language; setLanguage(l: Language): void; t(key: MessageKey | PluralBase, params?: Params): string; say(m: Message | Message[] | null | undefined): string; number(value: number, digits?: number): string; }`
  - `export const LANGUAGE_KEY = 'meander.language'`
- `number(value, digits)`: with `digits` given, exactly that many fraction digits; without, at most 1 (matches the old `formatKm`).
- `t` replaces `{name}` with `params[name]`; numeric params are formatted with `number(value)`, string params inserted as-is. Keys ending in `.one`/`.other` are chosen by `t(base, { count })` when `base + '.one'` exists: `count === 1` → `.one`.

- [ ] **Step 1: Write the dictionaries.**

`frontend/src/locales/en.ts`:

```ts
// Source dictionary. Every key here must also exist in nl.ts (enforced by its type).
export const en = {
  'brand.home': 'Meander home',
  'theme.group': 'Appearance',
  'theme.system': 'System theme',
  'theme.light': 'Light theme',
  'theme.dark': 'Dark theme',
  'language.group': 'Language',
  'language.nl': 'Nederlands',
  'language.en': 'English',
  'credit.routing': 'Routing & search:',
  'credit.data': 'Data:',
  'sheet.planner': 'Route planner',
  'sheet.show': 'Show the planner',
  'sheet.more': 'Show more map',
  'wizard.form': 'Plan a route',
  'step.start': 'Start',
  'step.distance': 'Distance',
  'step.preferences': 'Preferences',
  'step.back.start': 'Back to start',
  'step.back.distance': 'Back to distance',
  'step.progress': 'Step {current} of {total}',
  'start.title': 'Where do you start?',
  'activity.legend': 'Activity',
  'activity.running': 'Run',
  'activity.walking': 'Walk',
  'start.label': 'Start location',
  'start.placeholder': 'Search a town, park, or address',
  'start.locating': 'Finding you…',
  'start.useLocation': 'Use my location',
  'start.pickOnMap': 'Pick on map',
  'start.currentLocation': 'Current location',
  'distance.title': 'How far?',
  'distance.shorter': 'Half a kilometre shorter',
  'distance.longer': 'Half a kilometre longer',
  'distance.slider': 'Target distance',
  'distance.common': 'Common distances',
  'distance.quick': '{km} km',
  'distance.flexible': 'Flexible distance',
  'distance.between': 'Between',
  'distance.window': '{min} and {max} km',
  'distance.margin': 'Distance margin',
  'distance.stayClose': 'Stay close to home',
  'distance.maxRadius': 'Maximum radius',
  'preferences.title': 'What do you like?',
  'preferences.subtitle': 'All optional. Skip it if you just want to go.',
  'preferences.green': 'Green surroundings',
  'preferences.unpaved': 'Unpaved paths',
  'shape.heading': 'Route shape',
  'shape.finishElsewhere': 'Finish somewhere else',
  'shape.makeLoop': 'Make it a loop',
  'shape.sameAsStart': 'Same as start',
  'end.label': 'End location',
  'end.placeholder': 'Where would you like to finish?',
  'end.pickOnMap': 'Pick finish on map',
  'shape.passThrough': 'Pass through a spot',
  'shape.doneDrawing': 'Done drawing',
  'shape.editOnMap': 'Edit on map',
  'shape.drawOnMap': 'Draw on map',
  'drawing.section': 'Drawn route section',
  'drawing.points.one': '{count} point',
  'drawing.points.other': '{count} points',
  'drawing.drawnKm': '{km} km drawn',
  'drawing.km': '{km} km',
  'drawing.connecting': 'Connecting points along paths…',
  'drawing.lastValid': 'The last valid route is still shown. Undo or move a point to try again.',
  'drawing.undoTitle': 'Undo last change (Ctrl+Z)',
  'drawing.undo': 'Undo',
  'drawing.removePoint': 'Remove point {n}',
  'drawing.clear': 'Clear drawing',
  'drawing.refresh': 'Refresh drawing',
  'drawing.downloadEdited': 'Download edited GPX',
  'drawing.downloadDrawn': 'Download drawn GPX',
  'actions.find': 'Find my routes',
  'actions.finding': 'Finding your way…',
  'actions.opening': 'Opening saved routes…',
  'actions.cancel': 'Cancel',
  'actions.next.distance': 'Next: distance',
  'actions.next.preferences': 'Next: preferences',
  'actions.backToRoutes': 'Back to routes',
  'actions.openingEditor': 'Opening the route editor…',
  'summary.changeStart': 'Change start',
  'summary.changeDistance': 'Change distance',
  'summary.changePreferences': 'Change preferences',
  'summary.change': 'Change',
  'summary.start': '{activity} from {place}',
  'summary.margin': ', ±{margin}%',
  'summary.within': ', within {km} km',
  'summary.green': 'green',
  'summary.unpaved': 'unpaved',
  'summary.oneWay': 'one-way',
  'summary.drawn': 'drawn section',
  'summary.none': 'No preferences',
  'results.label': 'Route suggestions',
  'results.one': 'Your route',
  'results.many': '{count} routes for you',
  'results.savedLink': 'Saved route link',
  'results.downloadsUntil': 'GPX downloads available until {time}.',
  'results.none': 'No routes found',
  'results.noneHint': 'Try another start or adjust your distance and radius.',
  'route.name': 'Route {n}',
  'route.closest': 'Closest to {km} km',
  'route.distance': 'distance',
  'route.ascent': 'ascent',
  'route.noElevation': 'Elevation unavailable',
  'route.diagnostic': 'Exceeds radius · diagnostic',
  'route.outside': 'Outside target distance',
  'route.natureTitle': 'Approximate route length inside forest, heath, dunes or nature reserves mapped in OpenStreetMap',
  'route.nature': '{percent}% nature',
  'route.unpaved': '{percent}% confirmed unpaved',
  'route.edit': 'Edit',
  'route.editLabel': 'Edit route {n}',
  'route.preparing': 'Preparing…',
  'route.download': 'Download GPX',
  'edit.updating': 'Updating…',
  'edit.cancel': 'Cancel editing',
  'edit.save': 'Save',
  'edit.saving': 'Saving…',
  'edit.saveLabel': 'Save changes',
  'edit.retry': 'Retry',
  'edit.hint': 'Drag a point to reshape. Tap a small dot on the line to add one.',
  'edit.dismissHint': 'Dismiss tip',
  'edit.selected': 'Point {n} selected',
  'edit.toolbar': 'Route editing',
  'edit.tapMap': 'Tap map',
  'edit.add': 'Add',
  'edit.remove': 'Remove',
  'edit.removePoint': 'Remove point',
  'edit.fit': 'Fit',
  'error.chooseStart': 'Choose a start location from search or the map.',
  'error.distanceRange': 'Choose a distance between {min} and {max} km.',
  'error.radius': 'Choose a positive radius.',
  'error.tooManyPoints': 'This route has enough control points. Remove a point before adding another.',
  'error.noGeolocation': 'Your browser does not support location. Search for a place or choose one on the map.',
  'error.locationDenied': 'Location permission was denied. Search for a place or choose one on the map.',
  'error.locationUnavailable': 'Your location is unavailable. Search for a place or choose one on the map.',
  'error.chooseEnd': 'Choose an end location, or switch back to a loop.',
  'error.waitForDrawing': 'Wait for the drawn section to connect, or retry its preview.',
  'error.unreachable': 'Could not reach Meander. Check your connection and that the API is running, then try again.',
  'error.minimumDistance': 'Minimum distance: {km} km.',
  'error.retryAfter': 'Try again in {seconds} seconds.',
  'error.savedNoRoutes': 'This saved search has no routes.',
  'error.openSaved': 'Could not open these saved routes. Please try again.',
  'error.addLoopPoint': 'Add a point on the map to shape this loop.',
  'error.connectPoints': 'Could not connect these points. Move a point or try again.',
  'error.openEditor': 'Could not open this route for editing.',
  'error.saveEdited': 'Could not save the edited route.',
  'error.exportFailed': 'The export failed. Please try again.',
  'error.request': 'Unable to complete the request. Please try again.',
  'notice.cancelled': 'Search cancelled.',
  'notice.saved': 'Route saved.',
  'search.noResults': 'No places found. Try another search or use the map.',
  'search.unavailable': 'Search is unavailable. Try the map.',
  'search.searching': 'Searching',
  'search.selected': 'Location selected',
  'search.suggestions': '{label} suggestions',
  'map.label': 'Route map',
  'map.noWebgl': 'This browser could not start the map. Enable WebGL or try another browser. You can still search for places.',
  'map.tilesFailed': 'Map tiles could not load. Check your connection and map configuration.',
  'map.styleFailed': 'Map style could not load. Try again.',
  'map.drawnPoint': 'Drawn point {n}',
  'map.dragToMove': '{label} · drag to move',
  'map.addBetween': 'Add a point between {from} and {to}',
  'map.fromStart': 'start',
  'map.toFinish': 'finish',
  'map.pointN': 'point {n}',
  'map.addTitle': 'Tap to add a point · drag to place it',
  'map.tapToAdd': 'Tap the map to add a point',
  'map.tapToStart': 'Tap the map to set your start',
  'map.tapToFinish': 'Tap the map to set your finish',
  'map.unavailable': 'Map unavailable',
  'map.configure': 'Configure a basemap',
  'map.configureHint': 'Set VITE_MAPTILER_KEY or VITE_PMTILES_URL in frontend/.env.local and restart Vite to load the map.',
  'map.retry': 'Retry map',
  'map.diagnostic': 'Dashed routes exceed your radius',
  'warning.drawnPointSnapped': 'A clicked point was moved more than {meters} m to a walkable path. Check the drawn section on the map.',
  'warning.startSnapped': 'Start moved {meters} m to the nearest routable path.',
  'warning.endSnapped': 'End moved {meters} m to the nearest routable path.',
  'warning.partialSearch': 'Some routing attempts failed or timed out. These are the best routes found so far.',
  'warning.fewerRoutes': 'Found {found} distinct routes instead of {requested}. Try moving the start or changing the distance.',
  'warning.greenDataUnavailable': 'Not enough greenery data is available to compare this route’s green preference.',
  'warning.woodlandNotReached': 'No substantial nature area was reached within this distance. Try a longer route or a start nearer a forest or nature reserve.',
  'warning.radiusExceeded': 'Diagnostic route: reaches {km} km from start, beyond the {radiusKm} km radius.',
  'warning.radiusExceededEdited': 'The edited route exceeds your maximum radius.',
  'warning.roadRestrictions': 'There may be restrictions on some roads',
  'errors.invalidRequest': 'Check the route inputs.',
  'errors.editExpired': 'This edited route has expired. Refresh its preview before saving.',
  'errors.editMismatch': 'The edited route belongs to different settings. Refresh its preview before saving.',
  'errors.duplicatePoints': 'Choose different consecutive route points.',
  'errors.noPathNearPoint': 'No walkable path was found near one of the clicked points. Move that point closer to a path.',
  'errors.drawnSectionExpired': 'The drawn section has expired. Refresh its preview before generating routes.',
  'errors.drawnSectionMismatch': 'The drawn section belongs to different settings. Refresh its preview before generating routes.',
  'errors.pointOutsideRadius': 'A clicked route point is outside the radius. Move the point or increase the radius.',
  'errors.noPathNearStart': 'No walkable path was found near the selected location. Choose a point closer to a path.',
  'errors.drawnSectionTooLong': 'The drawn section is already longer than the requested route. Increase the distance or remove a point.',
  'errors.targetTooShort': 'The target is shorter than the shortest route between these locations.',
  'errors.drawnConnectionTooLong': 'The drawn section plus the shortest connection is longer than the requested route. Increase the distance or remove a point.',
  'errors.noUsableRoute': 'No usable route was found. The area may have water, disconnected paths, or a sparse walking network. Try another start or distance.',
  'errors.noUsableRouteBacktracking': 'You can also turn off out-and-back avoidance if no circular paths are available.',
  'errors.activityUnsupported': 'This activity is not supported by the configured provider.',
  'errors.noRoute': 'No routable path was found near the selected locations.',
  'errors.timeout': 'The routing service timed out. Try again shortly, or try a shorter distance.',
  'errors.invalidResponse': 'The routing service returned an invalid response.',
  'errors.unavailable': 'The routing service is temporarily unavailable. Try again shortly.',
  'errors.throttled': 'The routing service is at its request limit. Try again shortly.',
  'errors.malformedRequest': 'The request could not be read. Reload the page and try again.',
  'errors.serverError': 'Something went wrong. Please try again shortly.',
  'errors.tooManyRequests': 'Please wait before trying again.',
  'errors.savedSearchNotFound': 'This saved search is unavailable. Check the link or generate new routes.',
  'errors.routeNotFound': 'This route is unavailable. Open a saved route or generate a new one.',
  'errors.exportExpired': 'This route export has expired. Generate your routes again to download a new GPX file.',
  'errors.searchQueryLength': 'Search must contain 3–200 characters.',
  'errors.apiNotFound': 'This part of Meander is unavailable. Reload the page.',
  'errors.mapFileNotFound': 'A map file is missing.',
  'errors.notFound': 'Not found.',
  'errors.requestFailed': 'The request failed. Please try again.',
} as const;
```

`frontend/src/locales/nl.ts`:

```ts
import type { en } from './en';

export const nl: Record<keyof typeof en, string> = {
  'brand.home': 'Meander startpagina',
  'theme.group': 'Weergave',
  'theme.system': 'Systeemthema',
  'theme.light': 'Licht thema',
  'theme.dark': 'Donker thema',
  'language.group': 'Taal',
  'language.nl': 'Nederlands',
  'language.en': 'English',
  'credit.routing': 'Routering & zoeken:',
  'credit.data': 'Data:',
  'sheet.planner': 'Routeplanner',
  'sheet.show': 'Planner tonen',
  'sheet.more': 'Meer kaart tonen',
  'wizard.form': 'Plan een route',
  'step.start': 'Start',
  'step.distance': 'Afstand',
  'step.preferences': 'Voorkeuren',
  'step.back.start': 'Terug naar start',
  'step.back.distance': 'Terug naar afstand',
  'step.progress': 'Stap {current} van {total}',
  'start.title': 'Waar begin je?',
  'activity.legend': 'Activiteit',
  'activity.running': 'Hardlopen',
  'activity.walking': 'Wandelen',
  'start.label': 'Startlocatie',
  'start.placeholder': 'Zoek een plaats, park of adres',
  'start.locating': 'Locatie zoeken…',
  'start.useLocation': 'Gebruik mijn locatie',
  'start.pickOnMap': 'Kies op kaart',
  'start.currentLocation': 'Huidige locatie',
  'distance.title': 'Hoe ver?',
  'distance.shorter': 'Halve kilometer korter',
  'distance.longer': 'Halve kilometer langer',
  'distance.slider': 'Doelafstand',
  'distance.common': 'Veelgebruikte afstanden',
  'distance.quick': '{km} km',
  'distance.flexible': 'Flexibele afstand',
  'distance.between': 'Tussen',
  'distance.window': '{min} en {max} km',
  'distance.margin': 'Afstandsmarge',
  'distance.stayClose': 'Blijf dicht bij huis',
  'distance.maxRadius': 'Maximale straal',
  'preferences.title': 'Wat vind je fijn?',
  'preferences.subtitle': 'Allemaal optioneel. Sla het over als je gewoon wilt gaan.',
  'preferences.green': 'Groene omgeving',
  'preferences.unpaved': 'Onverharde paden',
  'shape.heading': 'Routevorm',
  'shape.finishElsewhere': 'Ergens anders eindigen',
  'shape.makeLoop': 'Maak er een rondje van',
  'shape.sameAsStart': 'Zelfde als start',
  'end.label': 'Eindlocatie',
  'end.placeholder': 'Waar wil je eindigen?',
  'end.pickOnMap': 'Kies eindpunt op kaart',
  'shape.passThrough': 'Langs een plek gaan',
  'shape.doneDrawing': 'Klaar met tekenen',
  'shape.editOnMap': 'Bewerk op kaart',
  'shape.drawOnMap': 'Teken op kaart',
  'drawing.section': 'Getekend routedeel',
  'drawing.points.one': '{count} punt',
  'drawing.points.other': '{count} punten',
  'drawing.drawnKm': '{km} km getekend',
  'drawing.km': '{km} km',
  'drawing.connecting': 'Punten verbinden via paden…',
  'drawing.lastValid': 'De laatste geldige route wordt nog getoond. Maak ongedaan of verplaats een punt om het opnieuw te proberen.',
  'drawing.undoTitle': 'Laatste wijziging ongedaan maken (Ctrl+Z)',
  'drawing.undo': 'Ongedaan maken',
  'drawing.removePoint': 'Punt {n} verwijderen',
  'drawing.clear': 'Tekening wissen',
  'drawing.refresh': 'Tekening vernieuwen',
  'drawing.downloadEdited': 'Bewerkte GPX downloaden',
  'drawing.downloadDrawn': 'Getekende GPX downloaden',
  'actions.find': 'Vind mijn routes',
  'actions.finding': 'Routes zoeken…',
  'actions.opening': 'Opgeslagen routes openen…',
  'actions.cancel': 'Annuleren',
  'actions.next.distance': 'Volgende: afstand',
  'actions.next.preferences': 'Volgende: voorkeuren',
  'actions.backToRoutes': 'Terug naar routes',
  'actions.openingEditor': 'Route-editor openen…',
  'summary.changeStart': 'Start wijzigen',
  'summary.changeDistance': 'Afstand wijzigen',
  'summary.changePreferences': 'Voorkeuren wijzigen',
  'summary.change': 'Wijzig',
  'summary.start': '{activity} vanaf {place}',
  'summary.margin': ', ±{margin}%',
  'summary.within': ', binnen {km} km',
  'summary.green': 'groen',
  'summary.unpaved': 'onverhard',
  'summary.oneWay': 'enkele reis',
  'summary.drawn': 'getekend deel',
  'summary.none': 'Geen voorkeuren',
  'results.label': 'Routesuggesties',
  'results.one': 'Jouw route',
  'results.many': '{count} routes voor jou',
  'results.savedLink': 'Link naar opgeslagen route',
  'results.downloadsUntil': 'GPX-downloads beschikbaar tot {time}.',
  'results.none': 'Geen routes gevonden',
  'results.noneHint': 'Probeer een andere start of pas je afstand en straal aan.',
  'route.name': 'Route {n}',
  'route.closest': 'Dichtst bij {km} km',
  'route.distance': 'afstand',
  'route.ascent': 'stijging',
  'route.noElevation': 'Hoogte onbekend',
  'route.diagnostic': 'Buiten straal · diagnose',
  'route.outside': 'Buiten doelafstand',
  'route.natureTitle': 'Geschat deel van de route door bos, heide, duinen of natuurgebieden volgens OpenStreetMap',
  'route.nature': '{percent}% natuur',
  'route.unpaved': '{percent}% bevestigd onverhard',
  'route.edit': 'Bewerken',
  'route.editLabel': 'Route {n} bewerken',
  'route.preparing': 'Voorbereiden…',
  'route.download': 'GPX downloaden',
  'edit.updating': 'Bijwerken…',
  'edit.cancel': 'Bewerken annuleren',
  'edit.save': 'Opslaan',
  'edit.saving': 'Opslaan…',
  'edit.saveLabel': 'Wijzigingen opslaan',
  'edit.retry': 'Opnieuw',
  'edit.hint': 'Sleep een punt om de route te vormen. Tik op een stipje op de lijn om er een toe te voegen.',
  'edit.dismissHint': 'Tip sluiten',
  'edit.selected': 'Punt {n} geselecteerd',
  'edit.toolbar': 'Route bewerken',
  'edit.tapMap': 'Tik op kaart',
  'edit.add': 'Toevoegen',
  'edit.remove': 'Verwijderen',
  'edit.removePoint': 'Punt verwijderen',
  'edit.fit': 'Passend',
  'error.chooseStart': 'Kies een startlocatie via zoeken of de kaart.',
  'error.distanceRange': 'Kies een afstand tussen {min} en {max} km.',
  'error.radius': 'Kies een positieve straal.',
  'error.tooManyPoints': 'Deze route heeft genoeg punten. Verwijder een punt voordat je er een toevoegt.',
  'error.noGeolocation': 'Je browser ondersteunt geen locatie. Zoek een plaats of kies er een op de kaart.',
  'error.locationDenied': 'Toestemming voor je locatie is geweigerd. Zoek een plaats of kies er een op de kaart.',
  'error.locationUnavailable': 'Je locatie is niet beschikbaar. Zoek een plaats of kies er een op de kaart.',
  'error.chooseEnd': 'Kies een eindlocatie, of maak er weer een rondje van.',
  'error.waitForDrawing': 'Wacht tot het getekende deel verbonden is, of probeer de voorbeeldweergave opnieuw.',
  'error.unreachable': 'Meander is niet bereikbaar. Controleer je verbinding en of de API draait, en probeer het opnieuw.',
  'error.minimumDistance': 'Minimale afstand: {km} km.',
  'error.retryAfter': 'Probeer het over {seconds} seconden opnieuw.',
  'error.savedNoRoutes': 'Deze opgeslagen zoekopdracht heeft geen routes.',
  'error.openSaved': 'Deze opgeslagen routes konden niet worden geopend. Probeer het opnieuw.',
  'error.addLoopPoint': 'Voeg een punt toe op de kaart om dit rondje vorm te geven.',
  'error.connectPoints': 'Deze punten konden niet worden verbonden. Verplaats een punt of probeer het opnieuw.',
  'error.openEditor': 'Deze route kon niet worden geopend om te bewerken.',
  'error.saveEdited': 'De bewerkte route kon niet worden opgeslagen.',
  'error.exportFailed': 'De export is mislukt. Probeer het opnieuw.',
  'error.request': 'Het verzoek kon niet worden voltooid. Probeer het opnieuw.',
  'notice.cancelled': 'Zoeken geannuleerd.',
  'notice.saved': 'Route opgeslagen.',
  'search.noResults': 'Geen plaatsen gevonden. Probeer een andere zoekterm of gebruik de kaart.',
  'search.unavailable': 'Zoeken is niet beschikbaar. Gebruik de kaart.',
  'search.searching': 'Zoeken',
  'search.selected': 'Locatie gekozen',
  'search.suggestions': 'Suggesties voor {label}',
  'map.label': 'Routekaart',
  'map.noWebgl': 'Deze browser kon de kaart niet starten. Zet WebGL aan of probeer een andere browser. Je kunt nog steeds plaatsen zoeken.',
  'map.tilesFailed': 'Kaarttegels konden niet laden. Controleer je verbinding en de kaartinstellingen.',
  'map.styleFailed': 'De kaartstijl kon niet laden. Probeer het opnieuw.',
  'map.drawnPoint': 'Getekend punt {n}',
  'map.dragToMove': '{label} · sleep om te verplaatsen',
  'map.addBetween': 'Voeg een punt toe tussen {from} en {to}',
  'map.fromStart': 'start',
  'map.toFinish': 'eindpunt',
  'map.pointN': 'punt {n}',
  'map.addTitle': 'Tik om een punt toe te voegen · sleep om het te plaatsen',
  'map.tapToAdd': 'Tik op de kaart om een punt toe te voegen',
  'map.tapToStart': 'Tik op de kaart om je start te kiezen',
  'map.tapToFinish': 'Tik op de kaart om je eindpunt te kiezen',
  'map.unavailable': 'Kaart niet beschikbaar',
  'map.configure': 'Stel een basiskaart in',
  'map.configureHint': 'Zet VITE_MAPTILER_KEY of VITE_PMTILES_URL in frontend/.env.local en herstart Vite om de kaart te laden.',
  'map.retry': 'Kaart opnieuw laden',
  'map.diagnostic': 'Gestippelde routes gaan buiten je straal',
  'warning.drawnPointSnapped': 'Een aangeklikt punt is meer dan {meters} m verplaatst naar een begaanbaar pad. Controleer het getekende deel op de kaart.',
  'warning.startSnapped': 'Start {meters} m verplaatst naar het dichtstbijzijnde pad.',
  'warning.endSnapped': 'Eindpunt {meters} m verplaatst naar het dichtstbijzijnde pad.',
  'warning.partialSearch': 'Sommige routepogingen zijn mislukt of duurden te lang. Dit zijn de beste routes tot nu toe.',
  'warning.fewerRoutes': 'Er zijn {found} verschillende routes gevonden in plaats van {requested}. Verplaats de start of wijzig de afstand.',
  'warning.greenDataUnavailable': 'Er zijn niet genoeg groengegevens om deze route op groen te vergelijken.',
  'warning.woodlandNotReached': 'Binnen deze afstand is geen noemenswaardig natuurgebied bereikt. Probeer een langere route of een start dichter bij een bos of natuurgebied.',
  'warning.radiusExceeded': 'Diagnoseroute: komt {km} km van de start, buiten de straal van {radiusKm} km.',
  'warning.radiusExceededEdited': 'De bewerkte route gaat buiten je maximale straal.',
  'warning.roadRestrictions': 'Op sommige wegen gelden mogelijk beperkingen',
  'errors.invalidRequest': 'Controleer de route-instellingen.',
  'errors.editExpired': 'Deze bewerkte route is verlopen. Vernieuw de voorbeeldweergave voordat je opslaat.',
  'errors.editMismatch': 'De bewerkte route hoort bij andere instellingen. Vernieuw de voorbeeldweergave voordat je opslaat.',
  'errors.duplicatePoints': 'Kies verschillende opeenvolgende routepunten.',
  'errors.noPathNearPoint': 'Er is geen begaanbaar pad gevonden bij een van de aangeklikte punten. Verplaats dat punt dichter naar een pad.',
  'errors.drawnSectionExpired': 'Het getekende deel is verlopen. Vernieuw de voorbeeldweergave voordat je routes zoekt.',
  'errors.drawnSectionMismatch': 'Het getekende deel hoort bij andere instellingen. Vernieuw de voorbeeldweergave voordat je routes zoekt.',
  'errors.pointOutsideRadius': 'Een aangeklikt routepunt ligt buiten de straal. Verplaats het punt of vergroot de straal.',
  'errors.noPathNearStart': 'Er is geen begaanbaar pad gevonden bij de gekozen locatie. Kies een punt dichter bij een pad.',
  'errors.drawnSectionTooLong': 'Het getekende deel is al langer dan de gevraagde route. Vergroot de afstand of verwijder een punt.',
  'errors.targetTooShort': 'Het doel is korter dan de kortste route tussen deze locaties.',
  'errors.drawnConnectionTooLong': 'Het getekende deel plus de kortste verbinding is langer dan de gevraagde route. Vergroot de afstand of verwijder een punt.',
  'errors.noUsableRoute': 'Er is geen bruikbare route gevonden. In dit gebied is misschien water, zijn paden niet verbonden of zijn er weinig wandelpaden. Probeer een andere start of afstand.',
  'errors.noUsableRouteBacktracking': 'Je kunt ook het vermijden van heen-en-terug uitzetten als er geen rondgaande paden zijn.',
  'errors.activityUnsupported': 'Deze activiteit wordt niet ondersteund door de ingestelde routeservice.',
  'errors.noRoute': 'Er is geen route gevonden bij de gekozen locaties.',
  'errors.timeout': 'De routeservice reageerde niet op tijd. Probeer het zo opnieuw of kies een kortere afstand.',
  'errors.invalidResponse': 'De routeservice gaf een ongeldig antwoord.',
  'errors.unavailable': 'De routeservice is tijdelijk niet beschikbaar. Probeer het zo opnieuw.',
  'errors.throttled': 'De routeservice zit aan zijn limiet. Probeer het zo opnieuw.',
  'errors.malformedRequest': 'Het verzoek kon niet worden gelezen. Herlaad de pagina en probeer het opnieuw.',
  'errors.serverError': 'Er ging iets mis. Probeer het zo opnieuw.',
  'errors.tooManyRequests': 'Wacht even voordat je het opnieuw probeert.',
  'errors.savedSearchNotFound': 'Deze opgeslagen zoekopdracht is niet beschikbaar. Controleer de link of zoek nieuwe routes.',
  'errors.routeNotFound': 'Deze route is niet beschikbaar. Open een opgeslagen route of zoek een nieuwe.',
  'errors.exportExpired': 'Deze export is verlopen. Zoek je routes opnieuw om een nieuw GPX-bestand te downloaden.',
  'errors.searchQueryLength': 'Een zoekopdracht moet 3 tot 200 tekens bevatten.',
  'errors.apiNotFound': 'Dit deel van Meander is niet beschikbaar. Herlaad de pagina.',
  'errors.mapFileNotFound': 'Er ontbreekt een kaartbestand.',
  'errors.notFound': 'Niet gevonden.',
  'errors.requestFailed': 'Het verzoek is mislukt. Probeer het opnieuw.',
};
```

- [ ] **Step 2: Write failing tests** `frontend/src/i18n.test.tsx`:

```tsx
import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, test, vi } from 'vitest';
import { detectLanguage, I18nProvider, LANGUAGE_KEY, useI18n } from './i18n';

function languages(list: string[]) {
  vi.spyOn(navigator, 'languages', 'get').mockReturnValue(list);
  vi.spyOn(navigator, 'language', 'get').mockReturnValue(list[0] ?? '');
}
afterEach(() => { vi.restoreAllMocks(); localStorage.clear(); });

test('detects Dutch, falls back to English, and prefers a saved choice', () => {
  languages(['nl-NL', 'en']); expect(detectLanguage()).toBe('nl');
  languages(['de-DE', 'en-GB']); expect(detectLanguage()).toBe('en');
  languages(['fr']); expect(detectLanguage()).toBe('en');
  localStorage.setItem(LANGUAGE_KEY, 'nl'); languages(['en-US']); expect(detectLanguage()).toBe('nl');
});

function Probe() {
  const { t, say, number, setLanguage, language } = useI18n();
  return <div>
    <p>{t('drawing.points', { count: 1 })}|{t('drawing.points', { count: 3 })}</p>
    <p>{say([{ key: 'error.minimumDistance', params: { km: number(2.5, 2) } }, { text: 'raw' }])}</p>
    <button onClick={() => setLanguage(language === 'nl' ? 'en' : 'nl')}>toggle</button>
  </div>;
}

test('translates with plurals, params and locale numbers, and persists a switch', async () => {
  languages(['en-US']);
  render(<I18nProvider><Probe /></I18nProvider>);
  expect(screen.getByText('1 point|3 points')).toBeInTheDocument();
  expect(screen.getByText('Minimum distance: 2.50 km. raw')).toBeInTheDocument();
  await act(() => userEvent.click(screen.getByRole('button', { name: 'toggle' })));
  expect(screen.getByText('1 punt|3 punten')).toBeInTheDocument();
  expect(screen.getByText('Minimale afstand: 2,50 km. raw')).toBeInTheDocument();
  expect(document.documentElement.lang).toBe('nl');
  expect(localStorage.getItem(LANGUAGE_KEY)).toBe('nl');
});
```

(If `@testing-library/user-event` is not in `frontend/package.json`, use `fireEvent.click` from `@testing-library/react` instead.)

- [ ] **Step 3: Run** `npm test -- i18n` in `frontend/` — Expected: FAIL, module `./i18n` not found.

- [ ] **Step 4: Implement** `frontend/src/i18n.tsx`:

```tsx
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { en } from './locales/en';
import { nl } from './locales/nl';

export type Language = 'nl' | 'en';
export type MessageKey = keyof typeof en;
export type PluralBase = 'drawing.points';
export type Params = Record<string, string | number>;
// UI state keeps descriptors rather than finished text, so a language switch re-renders it.
export type Message = { key: MessageKey; params?: Params } | { text: string };
export const LANGUAGE_KEY = 'meander.language';
const dictionaries: Record<Language, Record<MessageKey, string>> = { en, nl };

function savedLanguage(): Language | null {
  try {
    const value = window.localStorage.getItem(LANGUAGE_KEY);
    return value === 'nl' || value === 'en' ? value : null;
  } catch { return null; }
}

export function detectLanguage(): Language {
  const saved = savedLanguage();
  if (saved) return saved;
  const candidates = navigator.languages?.length ? navigator.languages : [navigator.language];
  for (const tag of candidates) {
    const primary = tag?.toLowerCase().split('-')[0];
    if (primary === 'nl' || primary === 'en') return primary;
  }
  return 'en';
}

type I18n = {
  language: Language;
  setLanguage(language: Language): void;
  t(key: MessageKey | PluralBase, params?: Params): string;
  say(message: Message | Message[] | null | undefined): string;
  number(value: number, digits?: number): string;
};
const I18nContext = createContext<I18n | null>(null);

export function I18nProvider({ children }: { children: ReactNode }) {
  const [language, setLanguageState] = useState<Language>(detectLanguage);
  useEffect(() => { document.documentElement.lang = language; }, [language]);
  const setLanguage = useCallback((next: Language) => {
    setLanguageState(next);
    try { window.localStorage.setItem(LANGUAGE_KEY, next); } catch { /* the choice lasts for this visit */ }
  }, []);
  const value = useMemo<I18n>(() => {
    const dictionary = dictionaries[language];
    const number = (value: number, digits?: number) => new Intl.NumberFormat(language,
      digits === undefined ? { maximumFractionDigits: 1 } : { minimumFractionDigits: digits, maximumFractionDigits: digits }).format(value);
    const t = (key: MessageKey | PluralBase, params?: Params) => {
      const resolved = (`${key}.one` in dictionary
        ? `${key}.${params?.count === 1 ? 'one' : 'other'}` : key) as MessageKey;
      const template = dictionary[resolved] ?? en[resolved] ?? key;
      return template.replace(/\{(\w+)\}/g, (match, name: string) => {
        const param = params?.[name];
        return param === undefined ? match : typeof param === 'number' ? number(param) : param;
      });
    };
    const say = (message: Message | Message[] | null | undefined): string =>
      !message ? '' : Array.isArray(message) ? message.map(say).filter(Boolean).join(' ')
        : 'text' in message ? message.text : t(message.key, message.params);
    return { language, setLanguage, t, say, number };
  }, [language, setLanguage]);
  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18n {
  const context = useContext(I18nContext);
  if (!context) throw new Error('useI18n must be used inside I18nProvider.');
  return context;
}
```

- [ ] **Step 5: Run** `npm test -- i18n` and `npx tsc -b` — Expected: PASS, no type errors (a missing Dutch key would fail `tsc`).

- [ ] **Step 6: Commit**

```bash
git add frontend/src/i18n.tsx frontend/src/i18n.test.tsx frontend/src/locales
git commit -m "Add Dutch and English dictionaries and language detection"
```

---

### Task 5: API text to messages

**Files:**
- Create: `frontend/src/apiText.ts`
- Modify: `frontend/src/types.ts` (warning `params`, problem `code`/`params`)
- Modify: `frontend/src/api.ts` (`ApiError` default message, `geocode` language)
- Test: `frontend/src/apiText.test.ts`, `frontend/src/api.test.ts`

**Interfaces:**
- Consumes: `Message`, `MessageKey` from `./i18n`.
- Produces:
  - `export function warningMessage(warning: RouteWarning, format: (value: number, digits?: number) => string): Message`
  - `export function problemMessages(error: ApiError, format: (value: number, digits?: number) => string): Message[]`
  - `export function failureMessages(failure: unknown, fallback: MessageKey, format: (value: number, digits?: number) => string): Message[]` — `ApiError` → `problemMessages`; `MessageError` → its message; anything else → `[{ key: fallback }]`.
  - `export class MessageError extends Error { constructor(public readonly content: Message) }`
  - `geocode(query: string, language: Language, signal: AbortSignal)`.
- `types.ts`: `export type RouteWarning = { code: string; message: string; params?: Record<string, number> | null };` and `RouteCandidate.warnings: RouteWarning[]`; `ApiProblem` gains `code?: string; params?: Record<string, number>`.

- [ ] **Step 1: Failing tests** `frontend/src/apiText.test.ts`:

```ts
import { expect, test } from 'vitest';
import { ApiError } from './api';
import { failureMessages, MessageError, problemMessages, warningMessage } from './apiText';

const format = (value: number, digits?: number) => digits === undefined ? String(value) : value.toFixed(digits);

test('translates shown warnings by code and falls back to the API text', () => {
  expect(warningMessage({ code: 'startSnapped', message: 'Start moved 40 m…', params: { meters: 40 } }, format))
    .toEqual({ key: 'warning.startSnapped', params: { meters: '40' } });
  expect(warningMessage({ code: 'radiusExceeded', message: 'The edited route…', params: { edited: 1 } }, format))
    .toEqual({ key: 'warning.radiusExceededEdited' });
  expect(warningMessage({ code: 'providerWarning', message: 'There may be restrictions on some roads', params: { orsCode: 1 } }, format))
    .toEqual({ key: 'warning.roadRestrictions' });
  // Saved searches from before params existed keep their English text.
  expect(warningMessage({ code: 'startSnapped', message: 'Start moved 40 m to the nearest routable path.' }, format))
    .toEqual({ text: 'Start moved 40 m to the nearest routable path.' });
  expect(warningMessage({ code: 'somethingNew', message: 'New text' }, format)).toEqual({ text: 'New text' });
});

test('translates problems by code with minimum distance and retry hints', () => {
  const error = new ApiError({ code: 'targetTooShort', detail: 'The target is shorter…', minimumDistanceKm: 2 }, '30');
  expect(problemMessages(error, format)).toEqual([
    { key: 'errors.targetTooShort' }, { key: 'error.minimumDistance', params: { km: '2.00' } }, { key: 'error.retryAfter', params: { seconds: '30' } }]);
  const backtracking = new ApiError({ code: 'noUsableRoute', params: { avoidBacktracking: 1 } });
  expect(problemMessages(backtracking, format)).toEqual([{ key: 'errors.noUsableRoute' }, { key: 'errors.noUsableRouteBacktracking' }]);
  expect(problemMessages(new ApiError({ code: 'brandNew', detail: 'Raw detail' }), format)).toEqual([{ text: 'Raw detail' }]);
});

test('maps other failures to a fallback or their own message', () => {
  expect(failureMessages(new TypeError('Failed to fetch'), 'error.openSaved', format)).toEqual([{ key: 'error.openSaved' }]);
  expect(failureMessages(new MessageError({ key: 'error.savedNoRoutes' }), 'error.openSaved', format)).toEqual([{ key: 'error.savedNoRoutes' }]);
});
```

In `api.test.ts` add a test that `geocode('Utrecht', 'nl', signal)` fetches `/api/geocode?q=Utrecht&lang=nl` (mirror the existing fetch-mock pattern in that file).

- [ ] **Step 2: Run** `npm test -- apiText api` — Expected: FAIL (module missing / geocode signature).

- [ ] **Step 3: Implement.**

`types.ts`: add `RouteWarning` as in Interfaces, use it for `warnings`, and add `code?: string; params?: Record<string, number>;` to `ApiProblem`.

`api.ts`:
- `ApiError` constructor: keep `super(problem.detail ?? problem.title ?? 'Unable to complete the request. Please try again.')` (English fallback for logs/tests).
- `geocode`:
```ts
export async function geocode(query: string, language: Language, signal: AbortSignal): Promise<Suggestion[]> {
  return (await checked(await fetch(`/api/geocode?q=${encodeURIComponent(query)}&lang=${language}`, { signal }))).json() as Promise<Suggestion[]>;
}
```
with `import type { Language } from './i18n';`.

`frontend/src/apiText.ts`:

```ts
import { ApiError } from './api';
import type { Message, MessageKey } from './i18n';
import { en } from './locales/en';
import type { RouteWarning } from './types';

type Format = (value: number, digits?: number) => string;

// A locally raised failure whose text is already a translatable message.
export class MessageError extends Error {
  constructor(public readonly content: Message) { super('text' in content ? content.text : content.key); }
}

const known = (key: string): key is MessageKey => key in en;

// Shown warnings are translated by code; anything unknown or without its numbers keeps the API's English text.
export function warningMessage(warning: RouteWarning, format: Format): Message {
  const params = warning.params ?? undefined;
  switch (warning.code) {
    case 'drawnPointSnapped': case 'startSnapped': case 'endSnapped':
      return params?.meters !== undefined
        ? { key: `warning.${warning.code}` as MessageKey, params: { meters: format(params.meters, 0) } } : { text: warning.message };
    case 'fewerRoutes':
      return params?.found !== undefined && params.requested !== undefined
        ? { key: 'warning.fewerRoutes', params: { found: format(params.found, 0), requested: format(params.requested, 0) } } : { text: warning.message };
    case 'radiusExceeded':
      if (params?.edited) return { key: 'warning.radiusExceededEdited' };
      return params?.km !== undefined && params.radiusKm !== undefined
        ? { key: 'warning.radiusExceeded', params: { km: format(params.km, 2), radiusKm: format(params.radiusKm, 2) } } : { text: warning.message };
    case 'partialSearch': case 'greenDataUnavailable': case 'woodlandNotReached':
      return { key: `warning.${warning.code}` as MessageKey };
    case 'providerWarning':
      return params?.orsCode === 1 ? { key: 'warning.roadRestrictions' } : { text: warning.message };
    default:
      return { text: warning.message };
  }
}

export function problemMessages(error: ApiError, format: Format): Message[] {
  const { problem } = error;
  const key = `errors.${problem.code}`;
  const messages: Message[] = problem.code && known(key) ? [{ key }] : [{ text: error.message }];
  if (problem.code === 'noUsableRoute' && problem.params?.avoidBacktracking) messages.push({ key: 'errors.noUsableRouteBacktracking' });
  if (problem.minimumDistanceKm !== undefined) messages.push({ key: 'error.minimumDistance', params: { km: format(problem.minimumDistanceKm, 2) } });
  if (error.retryAfter) messages.push({ key: 'error.retryAfter', params: { seconds: error.retryAfter } });
  return messages;
}

export function failureMessages(failure: unknown, fallback: MessageKey, format: Format): Message[] {
  if (failure instanceof ApiError) return problemMessages(failure, format);
  if (failure instanceof MessageError) return [failure.content];
  return [{ key: fallback }];
}
```

Note: `format(params.found, 0)` returns a string, which `t` inserts as-is; `seconds` from `Retry-After` is already a string.

- [ ] **Step 4: Run** `npm test -- apiText api` and `npx tsc -b` — Expected: PASS. (App still calls `geocode(query, signal)`; fix that call in Task 6, or temporarily pass `'en'` so `tsc` passes, then commit.)

- [ ] **Step 5: Commit**

```bash
git add frontend/src/apiText.ts frontend/src/apiText.test.ts frontend/src/types.ts frontend/src/api.ts frontend/src/api.test.ts
git commit -m "Translate API warnings and problems by code"
```

---

### Task 6: Translate the UI, toggle and map language

**Files:**
- Modify: `frontend/src/App.tsx`, `frontend/src/components/LocationSearch.tsx`, `frontend/src/components/RouteMap.tsx`, `frontend/src/mapStyle.ts`, `frontend/src/styles.css`
- Test: `frontend/src/App.test.tsx`, `frontend/src/mapStyle.test.ts`

**Interfaces:**
- Consumes: `I18nProvider`, `useI18n`, `Message`, `Language` from `./i18n`; `warningMessage`, `failureMessages`, `MessageError` from `./apiText`; `geocode(query, language, signal)`.
- Produces: `resolveMapStyle(env: MapEnv, scheme: ColorScheme, language: Language = 'nl', base = …)`.

- [ ] **Step 1: Failing tests.**

`mapStyle.test.ts`: add

```ts
test('protomaps labels follow the UI language', () => {
  const english = resolveMapStyle({ VITE_PMTILES_URL: '/map/netherlands.pmtiles' }, 'light', 'en', base).style as StyleSpecification;
  const dutch = resolveMapStyle({ VITE_PMTILES_URL: '/map/netherlands.pmtiles' }, 'light', 'nl', base).style as StyleSpecification;
  expect(JSON.stringify(english.layers)).toContain('name:en');
  expect(JSON.stringify(dutch.layers)).toContain('name:nl');
});
```
and update the existing `resolveMapStyle(env, scheme, base)` calls in that file to `resolveMapStyle(env, scheme, 'nl', base)`. (Import `StyleSpecification` type from `maplibre-gl` if not already imported.)

`App.test.tsx`: add (follow the file's existing render/mocking helpers; it already mocks `fetch` and the map):

```tsx
test('follows a Dutch browser and switches to English with the toggle', async () => {
  vi.spyOn(navigator, 'languages', 'get').mockReturnValue(['nl-NL']);
  render(<App />);
  expect(await screen.findByRole('heading', { name: 'Waar begin je?' })).toBeInTheDocument();
  expect(document.documentElement.lang).toBe('nl');
  fireEvent.click(screen.getByRole('button', { name: 'English' }));
  expect(await screen.findByRole('heading', { name: 'Where do you start?' })).toBeInTheDocument();
  expect(localStorage.getItem('meander.language')).toBe('en');
});

test('shows translated route warnings in Dutch', async () => {
  localStorage.setItem('meander.language', 'nl');
  // Reuse the file's generate-routes flow helper, returning one candidate with:
  // warnings: [{ code: 'startSnapped', message: 'Start moved 40 m to the nearest routable path.', params: { meters: 40 } }]
  // then assert:
  expect(await screen.findByText('Start 40 m verplaatst naar het dichtstbijzijnde pad.')).toBeInTheDocument();
});
```

For the second test, copy the arrange/act of the existing test that renders generated routes and checks `'61% nature'` (around line 226), change the candidate's warnings to the one above, and replace UI interactions with their Dutch labels (`'Vind mijn routes'`, `'Volgende: afstand'`, `'Volgende: voorkeuren'`). Add `afterEach(() => localStorage.removeItem('meander.language'))` if the file does not clear storage.

- [ ] **Step 2: Run** `npm test` — Expected: the new tests FAIL; existing ones pass.

- [ ] **Step 3: Implement.**

**3a. `mapStyle.ts`:** add `import type { Language } from './i18n';`, change `pmtilesStyle(archive, assets, scheme, base)` to `pmtilesStyle(archive, assets, scheme, language, base)` using `layers('protomaps', namedFlavor(flavor), { lang: language })`, and the export to:

```ts
export function resolveMapStyle(env: MapEnv, scheme: ColorScheme, language: Language = 'nl',
  base = globalThis.location?.href ?? 'http://localhost/'): ResolvedMapStyle {
```
passing `language` to `pmtilesStyle`.

**3b. App wrapper.** Rename the current `export default function App()` to `function Planner()` and add at the end of the file:

```tsx
export default function App() {
  return <I18nProvider><Planner /></I18nProvider>;
}
```
Inside `Planner` add `const { t, say, number, language, setLanguage } = useI18n();` and change module helpers that produce text to take `number`:

```ts
const formatKm = (km: number, number: (value: number, digits?: number) => string) => number(km);
```
(Replace every `formatKm(x)` call with `number(x)`, and every `x.toFixed(2)` shown to the user with `number(x, 2)`. `coordinateLabel` stays as is.)

Replace `QUESTION_STEPS` labels with keys: `{ step: 'start', label: 'step.start' }, { step: 'distance', label: 'step.distance' }, { step: 'preferences', label: 'step.preferences' }` typed `label: MessageKey`.

**3c. Message state.** Change:
```ts
const [error, setError] = useState<Message[]>([]);
const [notice, setNotice] = useState<Message | null>(null);
const [draftError, setDraftError] = useState<Message[]>([]);
```
Replace `setError('')` → `setError([])`, `setNotice('')` → `setNotice(null)`, `setDraftError('')` → `setDraftError([])`; truthiness checks `error &&` → `error.length > 0 &&`, `draftError &&` → `draftError.length > 0 &&`, `notice &&` stays. Rendering: `{say(error)}`, `{say(notice)}`, `{say(draftError)}`.

Replacements for each literal (left: current code, right: new code):

| Current | New |
|---|---|
| `throw new Error('This saved search has no routes.')` | `throw new MessageError({ key: 'error.savedNoRoutes' })` |
| `setError(failure instanceof Error ? failure.message : 'Could not open these saved routes. Please try again.')` | `setError(failureMessages(failure, 'error.openSaved', number))` |
| `setDraftError('Add a point on the map to shape this loop.')` | `setDraftError([{ key: 'error.addLoopPoint' }])` |
| `setDraftError(failure instanceof Error ? failure.message : 'Could not connect these points. Move a point or try again.')` | `setDraftError(failureMessages(failure, 'error.connectPoints', number))` |
| `setError('Choose a start location from search or the map.')` (3×) | `setError([{ key: 'error.chooseStart' }])` |
| ``setError(`Choose a distance between 0.5 and ${MAX_DISTANCE} km.`)`` (2×) | `setError([{ key: 'error.distanceRange', params: { min: number(.5), max: number(MAX_DISTANCE) } }])` |
| `setError('Choose a positive radius.')` (2×) | `setError([{ key: 'error.radius' }])` |
| `setError('This route has enough control points…')` | `setError([{ key: 'error.tooManyPoints' }])` |
| `setError('Your browser does not support location…')` | `setError([{ key: 'error.noGeolocation' }])` |
| `setStart({ label: 'Current location', location }); setStartText('Current location');` | `const label = t('start.currentLocation'); setStart({ label, location }); setStartText(label);` |
| `setError(failure.code === 1 ? 'Location permission was denied…' : 'Your location is unavailable…')` | `setError([{ key: failure.code === 1 ? 'error.locationDenied' : 'error.locationUnavailable' }])` |
| `setError('Choose an end location, or switch back to a loop.')` | `setError([{ key: 'error.chooseEnd' }])` |
| `setError('Wait for the drawn section to connect, or retry its preview.')` | `setError([{ key: 'error.waitForDrawing' }])` |
| the `if (ex instanceof ApiError) { … } else setError('Could not reach Meander…')` block in `submit` | `setError(failureMessages(ex, 'error.unreachable', number));` |
| `setNotice('Search cancelled.')` | `setNotice({ key: 'notice.cancelled' })` |
| `setError(failure instanceof Error ? failure.message : 'Could not open this route for editing.')` | `setError(failureMessages(failure, 'error.openEditor', number))` |
| `setNotice('Route saved.')` | `setNotice({ key: 'notice.saved' })` |
| `setError(failure instanceof Error ? failure.message : 'Could not save the edited route.')` | `setError(failureMessages(failure, 'error.saveEdited', number))` |
| `setError(ex instanceof ApiError && ex.problem.status === 404 ? 'This route export has expired…' : ex instanceof Error ? ex.message : 'The export failed. Please try again.')` | `setError(ex instanceof ApiError && ex.problem.status === 404 ? [{ key: 'errors.exportExpired' }] : failureMessages(ex, 'error.exportFailed', number))` |

The submit error previously appended field-level validation messages (`ex.problem.errors`); these are English-only and the UI prevents invalid input, so they are no longer shown.

**3d. JSX text.** Replace every visible string and `aria-label`/`title` with `t(...)`:

| Current | New |
|---|---|
| `distanceWindow` template | `t('distance.window', { min: number(Math.max(.5, km * (1 - margin)), 2), max: number(Math.min(MAX_DISTANCE, km * (1 + margin)), 2) })` |
| `preferenceSummary` array of `'green'`, `'unpaved'`, `'one-way'`, `'drawn section'`; `\|\| 'No preferences'` | `t('summary.green')`, `t('summary.unpaved')`, `t('summary.oneWay')`, `t('summary.drawn')`; `\|\| t('summary.none')` |
| `aria-label="Drawn route section"` | `aria-label={t('drawing.section')}` |
| `{drawnPoints.length} point{…'s'}` (2×) | `{t('drawing.points', { count: drawnPoints.length })}` |
| `{draft.distanceKm.toFixed(2)} km{completeDrawing ? '' : ' drawn'}` | `{t(completeDrawing ? 'drawing.km' : 'drawing.drawnKm', { km: number(draft.distanceKm, 2) })}` |
| `Connecting points along paths…` | `{t('drawing.connecting')}` |
| `' The last valid route is still shown…'` (2×) | `` ` ${t('drawing.lastValid')}` `` |
| `title="Undo last change (Ctrl+Z)"` (2×) | `title={t('drawing.undoTitle')}` |
| `Undo` (2×) | `{t('drawing.undo')}` |
| `Remove point {selectedPoint + 1}` | `{t('drawing.removePoint', { n: selectedPoint + 1 })}` |
| `Clear drawing` / `Refresh drawing` | `{t('drawing.clear')}` / `{t('drawing.refresh')}` |
| `'Download edited GPX' : 'Download drawn GPX'` | `t('drawing.downloadEdited') : t('drawing.downloadDrawn')` |
| `{warning.message}` / `{w.message}` in warning lists | `{say(warningMessage(warning, number))}` / `{say(warningMessage(w, number))}` |
| `` aria-label={`Back to ${…label.toLowerCase()}`} `` | `aria-label={t(questionIndex === 1 ? 'step.back.start' : 'step.back.distance')}` |
| `Step {questionIndex + 1} of 3` | `{t('step.progress', { current: questionIndex + 1, total: 3 })}` |
| `{QUESTION_STEPS[questionIndex].label}` | `{t(QUESTION_STEPS[questionIndex].label)}` |
| `'Where do you start?'` | `t('start.title')` |
| `Activity` legend | `{t('activity.legend')}` |
| `{value === 'running' ? 'Run' : 'Walk'}` (and the summary one) | `{t(value === 'running' ? 'activity.running' : 'activity.walking')}` |
| `label="Start location" placeholder="Search a town, park, or address"` | `label={t('start.label')} placeholder={t('start.placeholder')}` |
| `'Finding you…' : 'Use my location'` | `t('start.locating') : t('start.useLocation')` |
| `Pick on map` | `{t('start.pickOnMap')}` |
| `How far?` | `{t('distance.title')}` |
| `aria-label="Half a kilometre shorter"` / `longer` | `aria-label={t('distance.shorter')}` / `{t('distance.longer')}` |
| `aria-label="Target distance"` | `aria-label={t('distance.slider')}` |
| `aria-label="Common distances"` | `aria-label={t('distance.common')}` |
| `` aria-label={`${value} km`} `` + `{formatKm(value)}` | `aria-label={t('distance.quick', { km: number(value) })}` + `{number(value)}` |
| `Flexible distance` / `Between` | `{t('distance.flexible')}` / `{t('distance.between')}` |
| `Distance margin` / `Stay close to home` / `Maximum radius` | `{t('distance.margin')}` / `{t('distance.stayClose')}` / `{t('distance.maxRadius')}` |
| `'What do you like?', 'All optional…'` | `t('preferences.title'), t('preferences.subtitle')` |
| `Preferences` legend | `{t('step.preferences')}` |
| `Green surroundings` / `Unpaved paths` | `{t('preferences.green')}` / `{t('preferences.unpaved')}` |
| `Route shape` | `{t('shape.heading')}` |
| `Finish somewhere else`; `'Make it a loop' : 'Same as start'` | `{t('shape.finishElsewhere')}`; `t('shape.makeLoop') : t('shape.sameAsStart')` |
| `label="End location" placeholder="Where would you like to finish?"` | `label={t('end.label')} placeholder={t('end.placeholder')}` |
| `Pick finish on map` | `{t('end.pickOnMap')}` |
| `Pass through a spot`; `'Done drawing' : … 'Edit on map' : 'Draw on map'` | `{t('shape.passThrough')}`; `t('shape.doneDrawing') : … t('shape.editOnMap') : t('shape.drawOnMap')` |
| `aria-label="Change start"` / distance / preferences | `t('summary.changeStart')` / `t('summary.changeDistance')` / `t('summary.changePreferences')` |
| `<small>Start</small>…from {start?.label ?? '…'}` | `<small>{t('step.start')}</small>{t('summary.start', { activity: t(activity === 'running' ? 'activity.running' : 'activity.walking'), place: start?.label ?? '…' })}` |
| `Change` (3×) | `{t('summary.change')}` |
| `<small>Distance</small>{formatKm(km \|\| 0)} km{…±…}{…within…}` | `<small>{t('step.distance')}</small>{t('drawing.km', { km: number(km \|\| 0) })}{hasFlexibleDistance ? t('summary.margin', { margin: distanceMargin }) : ''}{hasRadius ? t('summary.within', { km: radius }) : ''}` |
| `<small>Preferences</small>` | `<small>{t('step.preferences')}</small>` |
| `aria-label="Route suggestions"` | `aria-label={t('results.label')}` |
| `'Your route' : \`${candidates.length} routes for you\`` | `t('results.one') : t('results.many', { count: candidates.length })` |
| `Route {index + 1}` | `{t('route.name', { n: index + 1 })}` |
| `Closest to {formatKm(km)} km` | `{t('route.closest', { km: number(km) })}` |
| `distance` / `ascent` dt | `{t('route.distance')}` / `{t('route.ascent')}` |
| `{candidate.distanceKm.toFixed(2)}` | `{number(candidate.distanceKm, 2)}` |
| `Elevation unavailable` | `{t('route.noElevation')}` |
| `'Exceeds radius · diagnostic' : 'Outside target distance'` | `t('route.diagnostic') : t('route.outside')` |
| nature `title=…` and `% nature` | `title={t('route.natureTitle')}` and `{t('route.nature', { percent: Math.round(candidate.woodlandPercent) })}` |
| `% confirmed unpaved` | `{t('route.unpaved', { percent: Math.round(candidate.unpavedPercent) })}` |
| `` aria-label={`Edit route ${index + 1}`} `` + `Edit` | `aria-label={t('route.editLabel', { n: index + 1 })}` + `{t('route.edit')}` |
| `'Preparing…' : 'Download GPX'` | `t('route.preparing') : t('route.download')` |
| `Saved route link` | `{t('results.savedLink')}` |
| `GPX downloads available until {…toLocaleTimeString([], …)}.` | `{t('results.downloadsUntil', { time: new Date(candidates[0].expiresAt).toLocaleTimeString(language, { hour: '2-digit', minute: '2-digit' }) })}` |
| `No routes found` / `Try another start…` | `{t('results.none')}` / `{t('results.noneHint')}` |
| `'Opening saved routes…' : 'Finding your way…'` (2×) | `t('actions.opening') : t('actions.finding')` |
| `Opening the route editor…` (2×) | `{t('actions.openingEditor')}` |
| `aria-label="Meander home"` | `aria-label={t('brand.home')}` |
| theme picker `aria-label="Appearance"`; `` `${Value} theme` `` | `aria-label={t('theme.group')}`; `t(\`theme.${value}\` as const)` for both `aria-label` and `title` |
| `Routing &amp; search:` / `Data:` | `{t('credit.routing')}` / `{t('credit.data')}` |
| `'Updating…'`; `` `${draft.distanceKm.toFixed(2)} km` `` (2×) | `t('edit.updating')`; `t('drawing.km', { km: number(draft.distanceKm, 2) })` (same for `lastGoodDraft`) |
| `aria-label="Cancel editing"` | `aria-label={t('edit.cancel')}` |
| `aria-label="Save changes"`; `'Saving…' : 'Save'` | `aria-label={t('edit.saveLabel')}`; `t('edit.saving') : t('edit.save')` |
| `Retry` | `{t('edit.retry')}` |
| `Drag a point to reshape…` | `{t('edit.hint')}` |
| `aria-label="Dismiss tip"` | `aria-label={t('edit.dismissHint')}` |
| `Point {selectedPoint + 1} selected` | `{t('edit.selected', { n: selectedPoint + 1 })}` |
| `aria-label="Route editing"` | `aria-label={t('edit.toolbar')}` |
| `'Tap map' : 'Add'` | `t('edit.tapMap') : t('edit.add')` |
| `` `Remove point ${selectedPoint + 1}` : 'Remove point' `` + `Remove` | `t('drawing.removePoint', { n: selectedPoint + 1 }) : t('edit.removePoint')` + `{t('edit.remove')}` |
| `Fit` | `{t('edit.fit')}` |
| `aria-label="Route planner"` | `aria-label={t('sheet.planner')}` |
| `'Show the planner' : 'Show more map'` | `t('sheet.show') : t('sheet.more')` |
| `aria-label="Plan a route"` | `aria-label={t('wizard.form')}` |
| `Find my routes` / `Cancel` | `{t('actions.find')}` / `{t('actions.cancel')}` |
| `Next: {view === 'start' ? 'distance' : 'preferences'}` | `{t(view === 'start' ? 'actions.next.distance' : 'actions.next.preferences')}` |
| `Back to routes` | `{t('actions.backToRoutes')}` |

Pass `language` to `RouteMap` (`language={language}` prop).

**3e. Language toggle.** Next to `themePicker`:

```tsx
const languagePicker = <div className="language-picker" role="group" aria-label={t('language.group')}>
  {(['nl', 'en'] as const).map(value => <button type="button" key={value} lang={value}
    aria-label={t(`language.${value}`)} aria-pressed={language === value} onClick={() => setLanguage(value)}>
    {value.toUpperCase()}</button>)}
</div>;
```
Render in the top bar as `<header className="top-bar" data-map-overlay="topbar">{brand}<div className="top-controls">{languagePicker}{themePicker}</div></header>`.

`styles.css`: style `.language-picker` like `.theme-picker` (copy its rules, selector list `.theme-picker, .language-picker` for the shared container/button rules) and add:

```css
.top-controls { display:flex; gap:8px; align-items:center; }
.language-picker button { width:auto; min-width:36px; padding:0 8px; font-size:13px; font-weight:650; }
```
In the wide-screen media query, include `.language-picker` wherever `.theme-picker` has `box-shadow:none`.

**3f. LocationSearch.** `const { t, language } = useI18n();`, `geocode(value.trim(), language, controller.signal)`, error state `useState<Message | null>(null)` rendered with `say`, and:
- `setError('No places found…')` → `setError({ key: 'search.noResults' })`
- catch → `setError(ex instanceof ApiError ? problemMessages(ex, number)[0] : { key: 'search.unavailable' })` (import `problemMessages` from `../apiText`, `number` from `useI18n()`)
- `aria-label="Searching"` → `t('search.searching')`; `"Location selected"` → `t('search.selected')`; `` `${label} suggestions` `` → `t('search.suggestions', { label })`
- add `language` to the effect dependency list.

**3g. RouteMap.** Add prop `language: Language`; `const { t } = useI18n();`.
- `resolveMapStyle(import.meta.env as MapEnv, props.colorScheme, props.language)` with deps `[props.colorScheme, props.language]`.
- Error state stores a `MessageKey | null` instead of text: `setError('map.noWebgl')`, `setError('map.tilesFailed')`, `setError('map.styleFailed')`; render `t(error)`.
- Marker labels: move the `aria-label`/`title` assignment out of the `if (!marker)` creation block so it runs for existing markers too, using `marker.getElement()`:
```ts
const label = typeof target === 'number' ? t('map.drawnPoint', { n: target + 1 }) : t(target === 'start' ? 'start.label' : 'end.label');
const element = marker.getElement();
element.setAttribute('aria-label', label); element.title = t('map.dragToMove', { label });
```
  and add `t` to that effect's dependency list.
- Ghost markers: `t('map.addBetween', { from: index === 0 ? t('map.fromStart') : t('map.pointN', { n: index }), to: index === anchors.length - 2 ? t('map.toFinish') : t('map.pointN', { n: index + 1 }) })`, `title = t('map.addTitle')`; add `t` to deps.
- JSX: `aria-label={t('map.label')}`; instruction: `props.mode === 'draw' ? props.start ? t('map.tapToAdd') : t('map.tapToStart') : t(props.mode === 'start' ? 'map.tapToStart' : 'map.tapToFinish')`; `t(error ? 'map.unavailable' : 'map.configure')`; `{error ? t(error) : t('map.configureHint')}`; `t('map.retry')`; `t('map.diagnostic')`.

- [ ] **Step 4: Run** `npx tsc -b && npm test` in `frontend/` — Expected: all pass, including existing English assertions (jsdom reports `en-US`).

- [ ] **Step 5: Run** `npm run test:e2e` — Expected: 6 passed.

- [ ] **Step 6: Commit**

```bash
git add frontend/src
git commit -m "Translate the planner into Dutch and English with a language toggle"
```

---

### Task 7: Dutch end-to-end check and docs

**Files:**
- Modify: `frontend/e2e/route.spec.ts`
- Modify: `README.md` (short "Languages" note)

- [ ] **Step 1: Add an E2E test** in `frontend/e2e/route.spec.ts`:

```ts
test.describe('Dutch browser', () => {
  test.use({ locale: 'nl-NL' });
  test('shows the planner in Dutch and switches to English', async ({ page }) => {
    await page.route('https://fonts.googleapis.com/**', route => route.abort());
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Waar begin je?' })).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('lang', 'nl');
    await page.getByRole('button', { name: 'English' }).click();
    await expect(page.getByRole('heading', { name: 'Where do you start?' })).toBeVisible();
  });
});
```

Existing E2E tests run with Playwright's default `en-US` locale and keep their English assertions.

- [ ] **Step 2: Run** `npm run test:e2e` — Expected: all pass (8 tests: 4 per project).

- [ ] **Step 3: README.** Under the user-facing feature description, add:

```markdown
### Languages

The planner is available in Dutch and English. It follows the browser language (Dutch for `nl-*`, English otherwise) until you pick **NL** or **EN** in the top bar; that choice is remembered in the browser. Map labels and place search follow the same language. The API keeps English `message`/`detail` texts and adds stable `code` values (and numeric `params` on warnings and some errors) that the frontend translates.
```

- [ ] **Step 4: Full verification**

Run from the repo root: `dotnet test Meander.slnx`; from `frontend/`: `npx tsc -b && npm test && npm run test:e2e`.
Expected: everything passes.

- [ ] **Step 5: Commit**

```bash
git add frontend/e2e/route.spec.ts README.md
git commit -m "Check the Dutch planner end to end and document languages"
```
