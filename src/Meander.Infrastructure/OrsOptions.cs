using Meander.Core;

namespace Meander.Infrastructure;

public sealed class OrsOptions
{
    public const string HostedApiHost = "api.openrouteservice.org";
    public string BaseUrl { get; set; } = "https://api.openrouteservice.org/";
    // Only the hosted API needs a key; a self-hosted ORS container accepts anonymous requests.
    public string ApiKey { get; set; } = "";
    // Self-hosted ORS has no green index unless it is built with one; it rejects green extras and weightings.
    public bool SupportsGreen { get; set; } = true;
    public int Concurrency { get; set; } = 3;
    public int AttemptTimeoutSeconds { get; set; } = 10;
    public int RetryCount { get; set; } = 2;
    public int DirectionsPerMinute { get; set; } = 35;
    public int GeocodingPerMinute { get; set; } = 90;
    public int SnapsPerMinute { get; set; } = 90;

    public bool IsHostedApi => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.Equals(HostedApiHost, StringComparison.OrdinalIgnoreCase);
}

public enum GeocodingProviderKind { Ors, Photon }

public sealed class GeocodingOptions
{
    public GeocodingProviderKind Provider { get; set; } = GeocodingProviderKind.Ors;
}

public sealed class PhotonOptions
{
    public string BaseUrl { get; set; } = "http://localhost:2322/";
    // Empty uses Photon's default (local) names; other values must be imported in the Photon index.
    public string Language { get; set; } = "";
}

public sealed class MapDataOptions
{
    // Directory served at /map, holding PMTiles and their glyphs/sprites. Empty disables it.
    public string Path { get; set; } = "";
}

public sealed class ActivityProfiles
{
    // New activities belong in the domain enum and this provider-specific registry.
    private readonly IReadOnlyDictionary<Activity, string> profiles = new Dictionary<Activity, string>
    {
        [Activity.Running] = "foot-walking", [Activity.Walking] = "foot-walking", [Activity.Cycling] = "cycling-regular"
    };
    public string For(Activity activity) => profiles.TryGetValue(activity, out var profile)
        ? profile : throw new RouteValidationException("activityUnsupported", "This activity is not supported by the configured provider.");
    // ORS offers the green weighting and extra info on foot profiles only.
    public bool SupportsGreen(Activity activity) => For(activity).StartsWith("foot-", StringComparison.Ordinal);
}
