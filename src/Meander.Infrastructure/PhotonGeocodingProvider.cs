using System.Text.Json;
using Meander.Core;
using static Meander.Infrastructure.ProviderJson;

namespace Meander.Infrastructure;

// Self-hostable OSM geocoder (https://github.com/komoot/photon), built for search-as-you-type.
public sealed class PhotonGeocodingProvider(HttpClient client, PhotonOptions options) : IGeocodingProvider
{
    public async Task<IReadOnlyList<GeocodeSuggestion>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        // One place is often several OSM objects (station node, building, platforms) with the
        // same label; over-fetch and keep the first of each label.
        var path = $"api?q={Uri.EscapeDataString(query)}&limit=15";
        if (!string.IsNullOrWhiteSpace(options.Language)) path += $"&lang={Uri.EscapeDataString(options.Language.Trim())}";
        using var response = await SendAsync(client, HttpMethod.Get, path, null, cancellationToken);
        using var json = await ReadAsync(response, cancellationToken);
        return Parse(() => json.RootElement.GetProperty("features").EnumerateArray().Select(feature =>
        {
            var props = feature.GetProperty("properties");
            var id = $"{Text(props, "osm_type")}{Text(props, "osm_id")}";
            return new GeocodeSuggestion(id.Length > 0 ? id : "photon", Label(props),
                ParseLocation(feature.GetProperty("geometry").GetProperty("coordinates")));
        }).DistinctBy(suggestion => suggestion.Label, StringComparer.OrdinalIgnoreCase).Take(5).ToArray());
    }

    // Photon returns address parts rather than a display label; join the useful ones.
    private static string Label(JsonElement props)
    {
        var street = string.Join(' ', new[] { Text(props, "street"), Text(props, "housenumber") }.Where(p => p.Length > 0));
        var name = Text(props, "name");
        var parts = new[] { name.Length > 0 ? name : street, name.Length > 0 ? street : "",
            Text(props, "city") is { Length: > 0 } city ? city : Text(props, "district"), Text(props, "country") };
        var label = string.Join(", ", parts.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        return label.Length > 0 ? label : "Unnamed location";
    }

    private static string Text(JsonElement props, string name) =>
        props.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        } : "";
}
