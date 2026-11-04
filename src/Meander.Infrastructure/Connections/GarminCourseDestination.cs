using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meander.Core;

namespace Meander.Infrastructure;

public sealed record GarminGeoPoint(double Latitude, double Longitude, double? Elevation);
public sealed record GarminCourse(string CourseName, string Description, double Distance, double ElevationGain,
    double ElevationLoss, string ActivityType, string CoordinateSystem, IReadOnlyList<GarminGeoPoint> GeoPoints);

// The only place that knows Garmin's course payload.
public static class GarminCourseMapper
{
    public static GarminCourse Map(RouteCandidate route, Activity activity)
    {
        var gain = route.ElevationGainM ?? 0;
        return new(
            $"Meander {route.DistanceKm.ToString("F1", CultureInfo.InvariantCulture)} km",
            "Planned with Meander",
            Math.Round(route.DistanceKm * 1000, 1),
            Math.Round(gain, 1),
            Math.Round(Descent(route.Geometry.Coordinates) ?? gain, 1),
            activity == Activity.Walking ? "WALKING" : "RUNNING",
            "WGS84",
            route.Geometry.Coordinates.Select(p => new GarminGeoPoint(p[1], p[0], p.Length > 2 ? p[2] : null)).ToArray());
    }

    // Garmin requires elevationLoss. Without an elevation on every point, fall back to the gain:
    // Meander routes usually end where they start, so they descend as much as they climb.
    private static double? Descent(IReadOnlyList<double[]> points) =>
        points.All(p => p.Length > 2)
            ? points.Zip(points.Skip(1), (a, b) => Math.Max(0, a[2] - b[2])).Sum()
            : null;
}

public sealed class GarminCourseDestination(HttpClient http, GarminOptions options) : IRouteDestination
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ServiceKind Service => ServiceKind.Garmin;

    public async Task<SentRoute> SendAsync(RouteCandidate route, Activity activity, ServiceTokens tokens, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.CourseUrl)
        {
            Content = JsonContent.Create(GarminCourseMapper.Map(route, activity), options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await http.SendAsync(request, ct);
        // 403: the user did not grant the courses permission on Garmin's consent screen.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderException(ProviderFailure.Unauthorized, "Garmin rejected the connection.");
        if (!response.IsSuccessStatusCode)
            throw new ProviderException(ProviderFailure.Unavailable,
                $"Garmin rejected the course ({(int)response.StatusCode}): {await ReasonAsync(response, ct)}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("courseId", out var value)
            ? value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null } : null;
        if (string.IsNullOrEmpty(id))
            throw new ProviderException(ProviderFailure.InvalidResponse, "Garmin returned no course id.");
        return new SentRoute(id, new Uri($"https://connect.garmin.com/modern/course/{Uri.EscapeDataString(id)}"));
    }

    // Garmin's error body explains which course field it rejected; keep it short and on one line for the log.
    private static async Task<string> ReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var line = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length == 0 ? "no details" : line.Length > 300 ? line[..300] + "…" : line;
    }
}
