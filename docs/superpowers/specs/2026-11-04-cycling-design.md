# Cycling routes

## Goal

Meander also plans cycling routes. Cycling sits next to running and walking as a third activity. It works with the same flows: round trips, A-to-B routes, drawing and editing, the nature and unpaved preferences, and the GPX, FIT and Garmin exports.

## Decisions

- **One cycling activity.** `Activity.Cycling` runs on the ORS `cycling-regular` profile. Road and MTB profiles are out of scope.
- **Distance limit per activity.** Cycling goes up to 100 km, running and walking stay at 60 km. 100 km is also the hard upper bound the startup check allows.
- **Both preferences stay.**
  - **Nature** uses the same Overpass woodland lookup. Anchors only sit on paths where cycling is allowed.
  - **Unpaved** uses the ORS surface extra info, which works the same for every profile.
- **Self-hosted ORS** builds a `cycling-regular` graph next to `foot-walking`.

## 1. Domain and validation

- The enum is `Activity { Running, Walking, Cycling }`.
- New option `Generation:MaxCyclingDistanceKm` (default 100). `GenerationOptions.MaxDistanceKmFor(activity)` returns it for cycling, and `MaxDistanceKm` for the other activities.
- Request validation and `RouteDistanceRange` use the maximum for the requested activity. The startup check bounds both maxima between 0.5 and 100.
- Stored searches and drafts keep the activity as a camelCase string in JSON, so no migration is needed.

## 2. Routing

- `ActivityProfiles` maps cycling to `cycling-regular`.
- `ActivityProfiles.SupportsGreen(activity)` is true only for foot profiles. ORS offers the green weighting only on those. `OrsProvider` sends the green extra and weighting only when both the configuration and the profile allow it.
- The error for no usable route mentions a sparse path network instead of a sparse walking network.

## 3. Nature anchors

- The Overpass query still fetches woodland polygons and paths. Its path filter keeps `access` and no longer filters on `foot`, so one cached response serves every activity.
- Parsing sorts the paths into two lists.
  - **On foot:** the current set of path types, with `foot` not `no` or `private`.
  - **By bike:**
    - Always included: `cycleway`, `track` and `living_street`.
    - Included only with `bicycle=yes|designated|permissive`: `path`, `footway`, `bridleway` and `pedestrian`.
    - Excluded in every case: paths with `bicycle=no|private`.
- `IWoodlandProvider.FindAsync` takes the activity. The provider caches the parsed lookup per centre and radius, and builds the `WoodlandMap` for each activity on first use.

## 4. Exports

- **FIT:** the course sport is `Cycling`. The synthetic pace for cycling is 20 km/h (0.18 s/m). Running and walking keep 6 min/km.
- **Garmin Courses API:** `activityType` is `ROAD_CYCLING`. Garmin's documentation was not available, so this value is checked against the live API. If Garmin rejects it, the logged reason guides the fix.
- **GPX:** unchanged. GPX carries no activity.

## 5. Frontend

- The activity picker offers Cycling ("Fietsen"), with a new `bike` icon.
- **Slider maximum:** depends on the activity. Running and walking use `VITE_MAX_DISTANCE_KM` (default 60). Cycling uses `VITE_MAX_CYCLING_DISTANCE_KM` (default 100).
- **Quick distances:** cycling uses 25, 40, 60, 80 and 100 km.
- Switching to an activity with a lower maximum clamps the chosen distance to that maximum.

## 6. Deployment

- `compose.local.yaml` and the Unraid `meander-services` stack enable `cycling-regular` with the `RoadAccessRestrictions` storage. ORS builds the missing graph on its next start; the existing `foot-walking` graph stays.
- Before raising the ORS heap, check the server's free memory.

## Out of scope

- **Road and MTB profiles.**
- **Pace or duration in the UI.**
- **A wider woodland search radius for long loops.** It stays capped at 12 km.
- **Strava.**
