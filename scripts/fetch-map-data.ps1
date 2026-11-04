# Downloads the data for running Meander without API keys (see README, "Run without API keys"):
#   data/map/netherlands.pmtiles   Protomaps basemap extract (served by the API at /map)
#   data/map/assets/               glyphs and sprites for the basemap style
#   data/ors/files/*.osm.pbf       OSM extract for the self-hosted ORS graph build
# Requires Docker (for the pmtiles CLI). Re-run with -Force to refresh existing files.
param(
    [string]$BBox = '3.2,50.7,7.3,53.6',
    [string]$OsmUrl = 'https://download.geofabrik.de/europe/netherlands-latest.osm.pbf',
    [int]$MaxZoom = 15,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$map = Join-Path $root 'data/map'
$ors = Join-Path $root 'data/ors/files'
New-Item -ItemType Directory -Force $map, $ors | Out-Null

$pmtiles = Join-Path $map 'netherlands.pmtiles'
if ($Force -or -not (Test-Path $pmtiles)) {
    # Daily planet builds are kept for a limited time; use the newest one that exists.
    $build = $null
    foreach ($daysAgo in 0..10) {
        $candidate = 'https://build.protomaps.com/{0:yyyyMMdd}.pmtiles' -f (Get-Date).ToUniversalTime().AddDays(-$daysAgo)
        try { Invoke-WebRequest -Method Head -Uri $candidate | Out-Null; $build = $candidate; break } catch { }
    }
    if (-not $build) { throw 'No recent Protomaps build found at https://build.protomaps.com/.' }
    Write-Host "Extracting $BBox from $build (only the needed tiles are downloaded)..."
    docker run --rm -v "${map}:/out" protomaps/go-pmtiles:v1.31.2 extract $build /out/netherlands.pmtiles "--bbox=$BBox" "--maxzoom=$MaxZoom"
    if ($LASTEXITCODE -ne 0) { throw 'pmtiles extract failed.' }
}

$assets = Join-Path $map 'assets'
if ($Force -or -not (Test-Path (Join-Path $assets 'sprites/v4'))) {
    Write-Host 'Downloading basemap glyphs and sprites...'
    $zip = Join-Path ([IO.Path]::GetTempPath()) "basemaps-assets-$([guid]::NewGuid()).zip"
    $unpacked = "$zip-dir"
    try {
        Invoke-WebRequest -Uri 'https://github.com/protomaps/basemaps-assets/archive/refs/heads/main.zip' -OutFile $zip
        Expand-Archive $zip $unpacked
        $source = Join-Path $unpacked 'basemaps-assets-main'
        Remove-Item -Recurse -Force $assets -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force $assets | Out-Null
        Copy-Item -Recurse (Join-Path $source 'fonts'), (Join-Path $source 'sprites') $assets
    } finally {
        Remove-Item -Force $zip -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force $unpacked -ErrorAction SilentlyContinue
    }
}

$pbf = Join-Path $ors (Split-Path -Leaf $OsmUrl)
if ($Force -or -not (Test-Path $pbf)) {
    Write-Host "Downloading $OsmUrl..."
    Invoke-WebRequest -Uri $OsmUrl -OutFile $pbf
    Write-Host 'Delete the ors-graphs volume (or set REBUILD_GRAPHS) so ORS rebuilds from the new extract.'
}
Write-Host "Done. Map data: $map, ORS source: $pbf"
