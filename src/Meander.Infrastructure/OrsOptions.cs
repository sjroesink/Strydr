using Meander.Core;

namespace Meander.Infrastructure;

public sealed class OrsOptions
{
    public string BaseUrl { get; set; } = "https://api.openrouteservice.org/";
    public string ApiKey { get; set; } = "";
    public int Concurrency { get; set; } = 3;
    public int AttemptTimeoutSeconds { get; set; } = 10;
    public int RetryCount { get; set; } = 2;
    public int DirectionsPerMinute { get; set; } = 35;
    public int GeocodingPerMinute { get; set; } = 90;
    public int SnapsPerMinute { get; set; } = 90;
}

public sealed class ActivityProfiles
{
    // New activities belong in the domain enum and this provider-specific registry.
    private readonly IReadOnlyDictionary<Activity, string> profiles = new Dictionary<Activity, string>
    {
        [Activity.Running] = "foot-walking", [Activity.Walking] = "foot-walking"
    };
    public string For(Activity activity) => profiles.TryGetValue(activity, out var profile)
        ? profile : throw new RouteValidationException("This activity is not supported by the configured provider.");
}
