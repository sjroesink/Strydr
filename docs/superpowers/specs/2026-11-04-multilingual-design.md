# Multilingual Meander (Dutch and English)

## Goal

The app is available in Dutch and English. The default comes from the browser; a visible toggle lets the user switch, and the choice is remembered. All user-facing text, including API warnings and errors, follows the chosen language. Saved routes switch language with the UI.

## Decisions

- Two languages: `nl` and `en`. Anything else falls back to English.
- A visible **NL | EN** toggle next to the theme picker.
- The frontend owns all user-facing text. The API sends stable codes and numeric parameters; English API text stays as a fallback for other clients and unknown codes.
- No i18n library: two typed dictionaries are enough for two languages and one client.

## 1. Language choice

New `frontend/src/i18n.ts` with an `I18nProvider` and `useI18n()` hook, in the style of `useTheme`.

Resolution order:

1. Saved choice in `localStorage` key `meander.language` (`'nl' | 'en'`). Reads and writes are wrapped in try/catch, as in `useTheme`.
2. The first entry of `navigator.languages` (or `navigator.language`) whose primary subtag is `nl` or `en`.
3. English.

Effects of the current language:

- `document.documentElement.lang` is set to it (`index.html` keeps `lang="en"` as the pre-script default).
- Map labels follow it for the self-hosted PMTiles/Protomaps basemap (MapTiler and custom `VITE_MAP_STYLE_URL` styles keep their own labels): `resolveMapStyle` takes the language and passes it to the Protomaps `layers(..., { lang })` call, which is now fixed to `'nl'`.
- Place search passes it to the API (section 3).

The toggle is a two-button group (`NL`, `EN`) with `aria-pressed`, styled like `.theme-picker`, placed beside it in the top bar. The group has an accessible label in the current language ("Taal" / "Language"). Each button is named by its visible text (`NL`, `EN`, per WCAG 2.5.3 Label in Name) and carries the full language name as a tooltip (`title`).

## 2. Translations

- `en` is the source dictionary. `nl` is typed as `Record<keyof typeof en, string>`, so a missing or extra Dutch key fails the TypeScript build.
- Keys are grouped by area (wizard steps, route cards, editing, map, warnings, errors).
- `t(key, params?)` replaces `{name}` placeholders. Strings needing a plural take a `count` parameter and have `one` and `other` forms.
- Numbers use `Intl.NumberFormat` for the current language, so Dutch shows `24,75 km` and English `24.75 km`. Existing formatting helpers (`formatKm`, coordinate labels) go through the same formatter. Coordinates keep a dot decimal separator in both languages, since they are input-like values.
- All hard-coded text in `App.tsx`, `LocationSearch.tsx` and `RouteMap.tsx` moves to the dictionaries, including `aria-label`s, titles and the page `<title>`.

## 3. API: codes and parameters

### Warnings

`RouteWarning` gains an optional `Params` dictionary (`IReadOnlyDictionary<string, double>?`). Only warnings the UI shows need translating; the hidden ones (`backtrackingRemoved`, `surfaceDataIncomplete`, `distanceMiss`, `qualityDataUnavailable`, `woodlandUnavailable`) keep their English message only. Shown warnings that embed numbers send them as parameters too:

| Code | Params |
|---|---|
| `drawnPointSnapped` | `meters` |
| `startSnapped`, `endSnapped` | `meters` |
| `fewerRoutes` | `found`, `requested` |
| `radiusExceeded` | `km`, `radiusKm` (generation); `edited: 1` (route editor) |

`message` stays in English. The frontend translates by `code` and falls back to `message` when the code is unknown, or when a code that needs parameters arrives without them (saved searches from before this change).

ORS warnings (`providerWarning`) carry the ORS message. The frontend translates known ORS texts by their ORS warning code where the API passes it (`orsCode` parameter), otherwise shows the message as-is.

### Errors

ProblemDetails responses gain a `code` extension, plus a `params` extension where the message has numbers:

- Endpoint problems: `tooManyRequests`, `savedSearchNotFound`, `routeNotFound`, `exportExpired`, `searchQueryLength`, `apiNotFound`.
- `RouteValidationException` gets a code per message (as implemented: `targetTooShort`, `drawnConnectionTooLong`, `noPathNearStart`, `noPathNearPoint`, `duplicatePoints`, `invalidRequest`, `editExpired`, `editMismatch`, `noUsableRoute` with `params.avoidBacktracking`, `activityUnsupported`). The minimum distance stays in the existing `minimumDistanceKm` ProblemDetails extension; there is no `minimumKm` param.
- `ProviderException` failures map to codes from `ProviderFailure`: `noRoute`, `timeout`, `invalidResponse`, `unavailable`, `throttled`. Other API-level codes are `malformedRequest`, `serverError`, `notFound`, `requestFailed` and `mapFileNotFound`.
- Field-level request validation (`RequestValidation.Check`) keeps its English per-field messages and gets the code `invalidRequest`; the UI already prevents these inputs.

`ApiError` in `frontend/src/api.ts` keeps `code` and `params`; the UI shows `t('errors.' + code, params)` when the code is known and the English `detail` otherwise. The frontend's own network-failure messages move into the dictionaries: a non-JSON failure response gets the frontend-only code `apiUnreachable` (shown as `error.unreachable`), distinct from the API's `unavailable`, which means the routing provider is down.

### Place search

`GET /api/geocode` takes an optional `lang` (`nl` or `en`). The Photon provider passes it as Photon's `lang` parameter, overriding the configured `Photon:Language`. The ORS geocoder ignores it.

## 4. Tests

Frontend (Vitest):

- Detection: `nl-NL` gives Dutch; `de-DE, en-GB` gives English; `fr` gives English; a saved choice beats the browser.
- The toggle switches visible text, `document.documentElement.lang` and the persisted value.
- Warnings: a coded warning with params renders in Dutch with Dutch number formatting; an unknown code and a params-less legacy warning fall back to the API message.
- Errors: a ProblemDetails with a known `code` renders translated; an unknown code shows `detail`.
- Existing tests keep running in English (jsdom reports `en-US`).

Backend (xUnit):

- Warnings with numbers carry the listed `Params`.
- ProblemDetails responses carry `code` (and `params` where listed).
- `/api/geocode?lang=nl` forwards `lang=nl` to Photon.

E2E (Playwright): one test with a `nl-NL` browser locale sees Dutch wizard text and a Dutch route badge.

## Out of scope

- Languages other than Dutch and English.
- GPX file contents and the README stay in English.
- Server-side translation of API text.
