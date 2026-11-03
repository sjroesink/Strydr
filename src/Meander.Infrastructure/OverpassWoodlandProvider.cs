using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Meander.Core;
using Microsoft.Extensions.Caching.Memory;

namespace Meander.Infrastructure;

public sealed class WoodlandOptions
{
    public string Url { get; set; } = "https://overpass-api.de/api/interpreter";
    public double MaxSearchRadiusKm { get; set; } = 12;
    public int TimeoutSeconds { get; set; } = 10;
    public int CacheHours { get; set; } = 12;
}

// Local development uses one cached area query, not requests per route or map movement.
// The provider is replaceable by a local OSM extract/service for production hosting.
public sealed class OverpassWoodlandProvider(IHttpClientFactory clients, WoodlandOptions options, TimeProvider clock)
    : IWoodlandProvider, IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
    private readonly SemaphoreSlim gate = new(1);
    private DateTimeOffset lastRequest = DateTimeOffset.MinValue;
    private sealed record Cached(WoodlandMap? Map);

    public async Task<WoodlandMap?> FindAsync(Location center, double radiusKm, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Cover the rounded centre's displacement, so cache reuse never loses the requested area.
        var queryCenter = new Location(Math.Round(center.Latitude, 2), Math.Round(center.Longitude, 2));
        var radius = Math.Min(options.MaxSearchRadiusKm + 1, Math.Ceiling(Math.Min(radiusKm, options.MaxSearchRadiusKm)) + 1);
        var key = (queryCenter.Latitude, queryCenter.Longitude, radius);
        if (cache.TryGetValue<Cached>(key, out var hit)) return hit!.Map;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var entered = false;
        try
        {
            await gate.WaitAsync(deadline.Token); entered = true;
            if (cache.TryGetValue<Cached>(key, out hit)) return hit!.Map;
            var gap = lastRequest.AddSeconds(1) - clock.GetUtcNow();
            if (gap > TimeSpan.Zero) await Task.Delay(gap, deadline.Token);
            lastRequest = clock.GetUtcNow();
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["data"] = Query(queryCenter, radius, Math.Max(1, options.TimeoutSeconds - 2))
            });
            using var response = await clients.CreateClient("woodland").PostAsync(options.Url, content, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(deadline.Token)
                ?? throw new JsonException("Empty woodland response.");
            var map = Parse(queryCenter, json.RootElement);
            cache.Set(key, new Cached(map), new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromHours(options.CacheHours))
                .SetSize(Math.Max(4096, json.RootElement.GetRawText().Length * 4L)));
            return map;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or KeyNotFoundException or FormatException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cache.Set(key, new Cached(null), new MemoryCacheEntryOptions().SetSize(128).SetAbsoluteExpiration(TimeSpan.FromMinutes(2)));
            return null;
        }
        finally { if (entered) gate.Release(); }
    }

    private static string Query(Location center, double radiusKm, int timeout)
    {
        var lat = center.Latitude.ToString("R", CultureInfo.InvariantCulture);
        var lon = center.Longitude.ToString("R", CultureInfo.InvariantCulture);
        var around = $"(around:{(radiusKm * 1000).ToString("F0", CultureInfo.InvariantCulture)},{lat},{lon})";
        return $"""
            [out:json][timeout:{timeout}][maxsize:16777216];
            is_in({lat},{lon})->.here;
            (
              wr{around}[natural=wood]; wr{around}[landuse=forest];
              way(pivot.here)[natural=wood]; rel(pivot.here)[natural=wood];
              way(pivot.here)[landuse=forest]; rel(pivot.here)[landuse=forest];
            ); out geom;
            way{around}[highway~"^(path|footway|track|bridleway|cycleway|pedestrian|living_street)$"]
              [access!~"^(no|private)$"][foot!~"^(no|private)$"]; out geom;
            """;
    }

    public static WoodlandMap Parse(Location center, JsonElement root)
    {
        // Overpass can return HTTP 200 with an incomplete result and a timeout remark.
        if (root.TryGetProperty("remark", out _)) throw new JsonException("Incomplete woodland response.");
        var polygons = new List<WoodlandPolygon>();
        var paths = new List<Location>();
        var elements = root.GetProperty("elements");
        if (elements.GetArrayLength() > 20000) throw new JsonException("Woodland response is too large.");
        foreach (var element in elements.EnumerateArray())
        {
            if (!element.TryGetProperty("tags", out var tags)) continue;
            string? Tag(string name) => tags.TryGetProperty(name, out var value) ? value.GetString() : null;
            var forest = Tag("natural") == "wood" || Tag("landuse") == "forest";
            var type = element.GetProperty("type").GetString();
            if (forest && type == "way" && element.TryGetProperty("geometry", out var geometry))
            {
                var ring = Points(geometry);
                if (Closed(ring)) polygons.Add(new(ring, []));
            }
            else if (forest && type == "relation" && Tag("type") == "multipolygon")
            {
                var outer = new List<Location[]>(); var inner = new List<Location[]>();
                foreach (var member in element.GetProperty("members").EnumerateArray())
                {
                    if (member.GetProperty("type").GetString() != "way" || !member.TryGetProperty("geometry", out geometry)) continue;
                    var role = member.GetProperty("role").GetString();
                    if (role is "outer" or "") outer.Add(Points(geometry));
                    else if (role == "inner") inner.Add(Points(geometry));
                }
                var holes = Rings(inner);
                foreach (var ring in Rings(outer))
                {
                    var shell = new WoodlandMap(center, [new(ring, [])], []);
                    polygons.Add(new(ring, holes.Where(h => shell.Contains(h[0])).ToArray()));
                }
            }
            if (type == "way" && Tag("highway") is "path" or "footway" or "track" or "bridleway" or "cycleway" or "pedestrian" or "living_street" &&
                Tag("access") is not ("no" or "private") && Tag("foot") is not ("no" or "private") &&
                element.TryGetProperty("geometry", out geometry))
                paths.AddRange(Geo.Sample(Points(geometry), .075));
        }
        return new(center, polygons, paths);
    }

    private static Location[] Points(JsonElement geometry)
    {
        if (geometry.GetArrayLength() > 100000) throw new JsonException("Woodland geometry is too large.");
        return geometry.EnumerateArray().Select(point =>
        {
            var location = new Location(point.GetProperty("lat").GetDouble(), point.GetProperty("lon").GetDouble());
            return Geo.IsValid(location) ? location : throw new JsonException("Invalid woodland coordinate.");
        }).ToArray();
    }

    private static bool Same(Location a, Location b) => Math.Abs(a.Latitude - b.Latitude) < 1e-8 && Math.Abs(a.Longitude - b.Longitude) < 1e-8;
    private static bool Closed(IReadOnlyList<Location> ring) => ring.Count >= 4 && Same(ring[0], ring[^1]);

    private static List<IReadOnlyList<Location>> Rings(List<Location[]> fragments)
    {
        var remaining = fragments.Where(f => f.Length >= 2).Select(f => f.ToList()).ToList();
        var rings = new List<IReadOnlyList<Location>>();
        while (remaining.Count > 0)
        {
            var ring = remaining[^1]; remaining.RemoveAt(remaining.Count - 1);
            while (!Closed(ring))
            {
                var next = remaining.FindIndex(f => Same(ring[^1], f[0]) || Same(ring[^1], f[^1]));
                if (next < 0) break;
                var part = remaining[next]; remaining.RemoveAt(next);
                if (Same(ring[^1], part[^1])) part.Reverse();
                ring.AddRange(part.Skip(1));
            }
            if (!Closed(ring)) throw new JsonException("Incomplete woodland boundary.");
            rings.Add(ring);
        }
        return rings;
    }

    public void Dispose() { cache.Dispose(); gate.Dispose(); }
}
