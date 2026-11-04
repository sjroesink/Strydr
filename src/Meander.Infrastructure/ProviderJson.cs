using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meander.Core;
using Polly.Timeout;

namespace Meander.Infrastructure;

// Shared HTTP/JSON handling for the ORS and Photon adapters.
internal static class ProviderJson
{
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        try { return await client.SendAsync(request, ct); }
        catch (TimeoutRejectedException ex)
        { throw new ProviderException(ProviderFailure.Timeout, "The routing service timed out.", ex); }
        catch (HttpRequestException ex)
        { throw new ProviderException(ProviderFailure.Unavailable, "The routing service is temporarily unavailable.", ex); }
    }

    public static Location ParseLocation(JsonElement coordinate)
    {
        if (coordinate.GetArrayLength() < 2) throw new JsonException("Missing coordinates.");
        double? elevation = coordinate.GetArrayLength() > 2 && coordinate[2].ValueKind == JsonValueKind.Number &&
            coordinate[2].TryGetDouble(out var height) && double.IsFinite(height) ? height : null;
        var location = new Location(coordinate[1].GetDouble(), coordinate[0].GetDouble(), elevation);
        if (!Geo.IsValid(location)) throw new JsonException("Invalid coordinates.");
        return location;
    }

    public static T Parse<T>(Func<T> parse, string? section = null)
    {
        try { return parse(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException)
        { throw new ProviderException(ProviderFailure.InvalidResponse, "The routing service returned an invalid response.", ex) { ResponseSection = section }; }
    }

    public static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ProviderException(ProviderFailure.Throttled, "The routing service is at its request limit. Try again shortly.");
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                // ORS codes 2009/2010/2011 identify missing paths or points. Other 4xx
                // responses mean a provider/configuration problem, not invalid user input.
                try
                {
                    using var error = await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
                    if (error?.RootElement.ValueKind == JsonValueKind.Object &&
                        error.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object &&
                        e.TryGetProperty("code", out var code) && code.TryGetInt32(out var number) && number is 2009 or 2010 or 2011)
                        throw new ProviderException(ProviderFailure.NoRoute, "No routable path was found near the selected locations.");
                }
                catch (JsonException) { /* Map malformed upstream errors below. */ }
            }
            throw new ProviderException((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? ProviderFailure.Unavailable : ProviderFailure.InvalidResponse,
                "The routing service could not complete the request. Check service availability and configuration.");
        }
        try
        {
            // HttpClient's maximum response buffer limits successful payloads to 16 MB.
            return await response.Content.ReadFromJsonAsync<JsonDocument>(ct)
                ?? throw new JsonException("Empty response.");
        }
        catch (JsonException ex)
        { throw new ProviderException(ProviderFailure.InvalidResponse, "The routing service returned invalid JSON.", ex); }
    }
}
