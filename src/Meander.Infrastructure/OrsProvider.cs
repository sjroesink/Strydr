using System.Text.Json;
using Meander.Core;
using static Meander.Infrastructure.ProviderJson;

namespace Meander.Infrastructure;

public sealed class OrsProvider(HttpClient client, ActivityProfiles profiles, OrsOptions options) : IRoutingProvider, IGeocodingProvider
{
    public async Task<IReadOnlyList<SnapResult?>> SnapAsync(IReadOnlyList<Location> points, Activity activity,
        double radiusM, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(client, HttpMethod.Post, $"v2/snap/{profiles.For(activity)}/json",
            new { locations = points.Select(Coordinate), radius = radiusM }, cancellationToken);
        using var json = await ReadAsync(response, cancellationToken);
        return Parse(() =>
        {
            var locations = json.RootElement.GetProperty("locations");
            if (locations.GetArrayLength() != points.Count) throw new JsonException("Unexpected snapped point count.");
            return locations.EnumerateArray().Select((item, index) =>
            {
                if (item.ValueKind == JsonValueKind.Null) return null;
                var snapped = ParseLocation(item.GetProperty("location"));
                // Measure from the authoritative coordinates. Optional ORS distance
                // metadata must never make an otherwise valid snapped point unusable.
                var distance = Geo.DistanceKm(points[index], snapped) * 1000;
                return new SnapResult(snapped, distance);
            }).ToArray();
        }, "snap");
    }

    public Task<RouteResult> RoundTripAsync(RoundTripQuery query, CancellationToken cancellationToken)
    {
        var body = MakeBody([query.Start], query.Preferences);
        var options = MakeOptions(query.Preferences);
        options["round_trip"] = new { length = query.DistanceKm * 1000, points = query.Points, seed = query.Seed };
        body["options"] = options;
        return DirectionsAsync(query.Activity, body, cancellationToken);
    }

    public Task<RouteResult> ViaPointsAsync(ViaPointsQuery query, CancellationToken cancellationToken)
    {
        // Keep the baseline truly shortest; scenery preferences must not inflate
        // the minimum distance used by validation.
        var preferences = query.Shortest ? null : query.Preferences;
        var body = MakeBody(query.Points, preferences);
        body["preference"] = query.Shortest ? "shortest" : "recommended";
        body["radiuses"] = query.Points.Select(_ => 1000).ToArray();
        var options = MakeOptions(preferences);
        if (options.Count > 0) body["options"] = options;
        return DirectionsAsync(query.Activity, body, cancellationToken);
    }

    private bool UseGreen(RoutePreferences? preferences) => preferences?.PreferGreen == true && options.SupportsGreen;

    private Dictionary<string, object> MakeBody(IReadOnlyList<Location> points, RoutePreferences? preferences)
    {
        var body = new Dictionary<string, object>
        {
            ["coordinates"] = points.Select(Coordinate), ["elevation"] = true, ["instructions"] = false
        };
        var extras = new List<string>();
        if (UseGreen(preferences)) extras.Add("green");
        if (preferences?.PreferUnpaved == true) extras.Add("surface");
        if (extras.Count > 0) body["extra_info"] = extras;
        return body;
    }

    private Dictionary<string, object> MakeOptions(RoutePreferences? preferences)
    {
        var options = new Dictionary<string, object>();
        if (UseGreen(preferences))
            options["profile_params"] = new { weightings = new { green = 1 } };
        return options;
    }

    private async Task<RouteResult> DirectionsAsync(Activity activity, object body, CancellationToken ct)
    {
        using var response = await SendAsync(client, HttpMethod.Post, $"v2/directions/{profiles.For(activity)}/geojson", body, ct);
        using var json = await ReadAsync(response, ct);
        return Parse(() =>
        {
            var feature = json.RootElement.GetProperty("features")[0];
            if (feature.GetProperty("geometry").GetProperty("type").GetString() != "LineString")
                throw new JsonException("Expected a LineString.");
            var geometry = feature.GetProperty("geometry").GetProperty("coordinates").EnumerateArray().Select(ParseLocation).ToArray();
            var properties = feature.GetProperty("properties");
            var summary = properties.GetProperty("summary");
            double? ascent = null;
            if ((summary.TryGetProperty("ascent", out var a) || properties.TryGetProperty("ascent", out a)) &&
                a.ValueKind == JsonValueKind.Number && a.TryGetDouble(out var elevation) && double.IsFinite(elevation) && elevation >= 0)
                ascent = elevation;
            var warnings = properties.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
                ? w.EnumerateArray().Select(item =>
                {
                    var message = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString()! : "The provider reported a route warning.";
                    // ORS warning codes let the UI translate known provider texts.
                    var code = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var number)
                        ? new Dictionary<string, double> { ["orsCode"] = number } : null;
                    return new RouteWarning("providerWarning", message, code);
                }).ToList() : [];
            SegmentQuality[]? qualities;
            try { qualities = ParseQualities(properties, geometry.Length); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // Optional surface/green spans can be inconsistent with merged geometry.
                // Drop the whole quality dataset, never fabricate values or lose the path.
                qualities = null;
                warnings.Add(new("qualityDataUnavailable", "Surface and greenery details could not be read for this route."));
            }
            return new RouteResult(geometry, summary.GetProperty("distance").GetDouble() / 1000, ascent, warnings,
                qualities);
        }, "directions");
    }

    private static SegmentQuality[]? ParseQualities(JsonElement properties, int vertexCount)
    {
        if (vertexCount < 2 || !properties.TryGetProperty("extras", out var extras)) return null;
        var qualities = Enumerable.Range(0, vertexCount - 1).Select(_ => new SegmentQuality()).ToArray();
        foreach (var name in new[] { "green", "surface" })
        {
            if (!extras.TryGetProperty(name, out var data) || !data.TryGetProperty("values", out var values)) continue;
            foreach (var span in values.EnumerateArray())
            {
                if (span.GetArrayLength() != 3) throw new JsonException("Invalid extra-info span.");
                var from = span[0].GetInt32(); var to = span[1].GetInt32(); var value = span[2].GetInt32();
                if (from < 0 || to < from || to >= vertexCount) throw new JsonException("Extra-info span outside the route.");
                for (var i = from; i < to; i++)
                    qualities[i] = name == "green"
                        ? qualities[i] with { GreenScore = value is >= 0 and <= 10 ? value / 10.0 : null }
                        : qualities[i] with { Unpaved = IsUnpaved(value) };
            }
        }
        return qualities;
    }

    // ORS surface IDs: unknown/snow remain unknown; grass pavers and wood are
    // constructed surfaces. Legacy fine-gravel/woodchip IDs remain compatible.
    private static bool? IsUnpaved(int value) => value switch
    {
        2 or 8 or 9 or 10 or 11 or 12 or 15 or 16 or 17 => true,
        1 or 3 or 4 or 5 or 6 or 7 or 14 or 18 => false,
        _ => null
    };

    public async Task<IReadOnlyList<GeocodeSuggestion>> SearchAsync(string query, string? language, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(client, HttpMethod.Get, $"geocode/autocomplete?text={Uri.EscapeDataString(query)}&size=5", null, cancellationToken);
        using var json = await ReadAsync(response, cancellationToken);
        return Parse(() => json.RootElement.GetProperty("features").EnumerateArray().Select(feature =>
        {
            var props = feature.GetProperty("properties");
            return new GeocodeSuggestion(props.GetProperty("gid").GetString() ?? "",
                props.GetProperty("label").GetString() ?? "Unnamed location",
                ParseLocation(feature.GetProperty("geometry").GetProperty("coordinates")));
        }).ToArray());
    }

    private static double[] Coordinate(Location point) => [point.Longitude, point.Latitude];
}
