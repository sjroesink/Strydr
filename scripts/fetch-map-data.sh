#!/usr/bin/env sh
# Downloads the data for running Meander without API keys (see README, "Run without API keys"):
#   data/map/netherlands.pmtiles   Protomaps basemap extract (served by the API at /map)
#   data/map/assets/               glyphs and sprites for the basemap style
#   data/ors/files/*.osm.pbf       OSM extract for the self-hosted ORS graph build
# Requires Docker (for the pmtiles CLI), curl and unzip. Set FORCE=1 to refresh existing files.
set -eu
BBOX="${BBOX:-3.2,50.7,7.3,53.6}"
OSM_URL="${OSM_URL:-https://download.geofabrik.de/europe/netherlands-latest.osm.pbf}"
MAX_ZOOM="${MAX_ZOOM:-15}"
FORCE="${FORCE:-0}"
root="$(cd "$(dirname "$0")/.." && pwd)"
map="$root/data/map"
ors="$root/data/ors/files"
mkdir -p "$map" "$ors"

if [ "$FORCE" = 1 ] || [ ! -f "$map/netherlands.pmtiles" ]; then
  # Daily planet builds are kept for a limited time; use the newest one that exists.
  build=""
  for days in 0 1 2 3 4 5 6 7 8 9 10; do
    date="$(date -u -d "-$days days" +%Y%m%d 2>/dev/null || date -u -v-"$days"d +%Y%m%d)"
    if curl -fsI "https://build.protomaps.com/$date.pmtiles" >/dev/null; then build="https://build.protomaps.com/$date.pmtiles"; break; fi
  done
  [ -n "$build" ] || { echo "No recent Protomaps build found at https://build.protomaps.com/." >&2; exit 1; }
  echo "Extracting $BBOX from $build (only the needed tiles are downloaded)..."
  docker run --rm -v "$map:/out" protomaps/go-pmtiles:v1.31.2 extract "$build" /out/netherlands.pmtiles "--bbox=$BBOX" "--maxzoom=$MAX_ZOOM"
fi

if [ "$FORCE" = 1 ] || [ ! -d "$map/assets/sprites/v4" ]; then
  echo "Downloading basemap glyphs and sprites..."
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  curl -fsSL -o "$tmp/assets.zip" https://github.com/protomaps/basemaps-assets/archive/refs/heads/main.zip
  unzip -q "$tmp/assets.zip" -d "$tmp"
  rm -rf "$map/assets" && mkdir -p "$map/assets"
  cp -R "$tmp/basemaps-assets-main/fonts" "$tmp/basemaps-assets-main/sprites" "$map/assets/"
fi

pbf="$ors/$(basename "$OSM_URL")"
if [ "$FORCE" = 1 ] || [ ! -f "$pbf" ]; then
  echo "Downloading $OSM_URL..."
  curl -fL -o "$pbf" "$OSM_URL"
  echo "Delete the ors-graphs volume (or set REBUILD_GRAPHS) so ORS rebuilds from the new extract."
fi
echo "Done. Map data: $map, ORS source: $pbf"
