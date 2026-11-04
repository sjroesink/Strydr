using System.Text.Json.Serialization;

namespace Meander.Core;

public enum Activity { Running, Walking }
public enum CandidateStatus { Qualified, BestEffort, Diagnostic }

public sealed record Location([property: JsonRequired] double Latitude,
    [property: JsonRequired] double Longitude, double? ElevationM = null);
public sealed record RouteRequest(
    Location? Start, Location? End, double? RadiusKm, double TargetDistanceKm,
    Activity Activity = Activity.Running, int CandidateCount = 3,
    bool PreferGreen = false, bool PreferUnpaved = false, bool AvoidBacktracking = true,
    double DistanceFlexibilityPercent = 0, Guid? DrawnRouteId = null, bool IsEditedRoute = false);
public sealed record RouteEditSession(RouteCandidate Preview, RouteRequest Request,
    IReadOnlyList<Location> Points, bool IsLoop);
public sealed record RoutePreviewRequest(Location? Start, IReadOnlyList<Location>? Points,
    Activity Activity = Activity.Running, bool PreferGreen = false, bool PreferUnpaved = false);
public sealed record RouteDraft(RouteResult Route, EndpointInfo Start, EndpointInfo LastPoint,
    IReadOnlyList<Location> RequestedPoints, Activity Activity, RoutePreferences Preferences);
// Message stays English for API clients; Code and Params let the UI translate it.
public sealed record RouteWarning(string Code, string Message, IReadOnlyDictionary<string, double>? Params = null);
public sealed record EndpointInfo(Location Requested, Location Snapped, double SnapDistanceM);
public sealed record LineString(double[][] Coordinates)
{
    public string Type => "LineString";
    public static LineString From(IReadOnlyList<Location> points) => new(points.Select(p =>
        p.ElevationM is { } elevation ? new[] { p.Longitude, p.Latitude, elevation }
            : new[] { p.Longitude, p.Latitude }).ToArray());
}
public sealed record RouteCandidate(
    Guid Id, DateTimeOffset? ExpiresAt, LineString Geometry, double DistanceKm,
    double? ElevationGainM, double MaxDistanceFromStartKm, double Score,
    CandidateStatus Status, EndpointInfo Start, EndpointInfo End, RouteWarning[] Warnings,
    double? GreenScore = null, double? UnpavedPercent = null, double? SurfaceKnownPercent = null,
    IReadOnlyList<Location>? SnappedPoints = null, Guid? SearchId = null,
    double? WoodlandPercent = null);
public sealed record SavedRouteSearch(Guid Id, DateTimeOffset CreatedAt, RouteRequest Request,
    IReadOnlyList<RouteCandidate> Candidates, IReadOnlyList<Location> DrawnPoints, RouteCandidate? DrawnPreview);
public interface IRouteSearchStore
{
    SavedRouteSearch Save(RouteRequest request, IReadOnlyList<RouteCandidate> candidates,
        RouteDraft? draft = null, RouteCandidate? preview = null);
    SavedRouteSearch? Get(Guid id);
    RouteCandidate? GetCandidate(Guid id);
    RouteDraft? GetDraft(Guid id);
}
public sealed record RouteResult(IReadOnlyList<Location> Geometry, double DistanceKm,
    double? ElevationGainM, IReadOnlyList<RouteWarning>? Warnings = null,
    IReadOnlyList<SegmentQuality>? SegmentQualities = null);
// One quality entry per geometry edge. Unknown data remains null.
public sealed record SegmentQuality(double? GreenScore = null, bool? Unpaved = null);
public sealed record RoutePreferences(bool PreferGreen = false, bool PreferUnpaved = false);
public sealed record SnapResult(Location Location, double DistanceM);
public sealed record GeocodeSuggestion(string Id, string Label, Location Location);
public sealed record RoundTripQuery(Location Start, double DistanceKm, int Points, int Seed, Activity Activity,
    RoutePreferences? Preferences = null);
public sealed record ViaPointsQuery(IReadOnlyList<Location> Points, Activity Activity, bool Shortest = false,
    RoutePreferences? Preferences = null);

public interface IRoutingProvider
{
    Task<IReadOnlyList<SnapResult?>> SnapAsync(IReadOnlyList<Location> points, Activity activity,
        double radiusM, CancellationToken cancellationToken);
    Task<RouteResult> RoundTripAsync(RoundTripQuery query, CancellationToken cancellationToken);
    Task<RouteResult> ViaPointsAsync(ViaPointsQuery query, CancellationToken cancellationToken);
}
public interface IGeocodingProvider
{
    Task<IReadOnlyList<GeocodeSuggestion>> SearchAsync(string query, CancellationToken cancellationToken);
}
public interface ICandidateStore
{
    void Put(RouteCandidate candidate);
    RouteCandidate? Get(Guid id);
    void PutDraft(Guid id, RouteDraft draft, DateTimeOffset expiresAt);
    RouteDraft? GetDraft(Guid id);
}

public sealed class GenerationOptions
{
    public double MaxDistanceKm { get; set; } = 60;
    public double SnapRadiusM { get; set; } = 1000;
    public double SnapWarningM { get; set; } = 200;
    public double DistanceTolerance { get; set; } = .05;
    public int DeadlineSeconds { get; set; } = 45;
    public int CacheTtlMinutes { get; set; } = 30;
}

public sealed record RouteDistanceRange(double MinKm, double MaxKm)
{
    public bool Contains(double distanceKm) => distanceKm >= MinKm - .000001 && distanceKm <= MaxKm + .000001;
    public static RouteDistanceRange For(RouteRequest request, GenerationOptions options)
    {
        var tolerance = Math.Max(options.DistanceTolerance, request.DistanceFlexibilityPercent / 100);
        return new(Math.Max(.5, request.TargetDistanceKm * (1 - tolerance)),
            Math.Min(options.MaxDistanceKm, request.TargetDistanceKm * (1 + tolerance)));
    }
}

public sealed class RouteValidationException(string code, string message,
    IDictionary<string, string[]>? errors = null, double? minimumDistanceKm = null,
    IReadOnlyDictionary<string, double>? parameters = null) : Exception(message)
{
    // Stable code for clients that translate the message.
    public string Code { get; } = code;
    public IDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
    public double? MinimumDistanceKm { get; } = minimumDistanceKm;
    public IReadOnlyDictionary<string, double>? Params { get; } = parameters;
}
public enum ProviderFailure { Unavailable, Throttled, Timeout, InvalidResponse, NoRoute }
public sealed class ProviderException(ProviderFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ProviderFailure Failure { get; } = failure;
    public string? ResponseSection { get; init; }
}

public static class ProviderFailures
{
    public static string Code(this ProviderFailure failure) => failure switch
    {
        ProviderFailure.NoRoute => "noRoute",
        ProviderFailure.Timeout => "timeout",
        ProviderFailure.Throttled => "throttled",
        ProviderFailure.InvalidResponse => "invalidResponse",
        _ => "unavailable"
    };
}

public static class RequestValidation
{
    public static Dictionary<string, string[]> Check(RouteRequest request, GenerationOptions options)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Geo.IsValid(request.Start)) errors["start"] = ["Choose a valid start location."];
        if (request.End is not null && !Geo.IsValid(request.End)) errors["end"] = ["Choose a valid end location."];
        if (!double.IsFinite(request.TargetDistanceKm) || request.TargetDistanceKm < .5 || request.TargetDistanceKm > options.MaxDistanceKm)
            errors["targetDistanceKm"] = [$"Distance must be between 0.5 and {options.MaxDistanceKm} km."];
        if (request.RadiusKm is { } radius && (!double.IsFinite(radius) || radius <= 0))
            errors["radiusKm"] = ["Radius must be a positive number."];
        if (!Enum.IsDefined(request.Activity)) errors["activity"] = ["Choose Running or Walking."];
        if (request.CandidateCount is < 1 or > 3) errors["candidateCount"] = ["Request between one and three candidates."];
        if (request.DrawnRouteId == Guid.Empty) errors["drawnRouteId"] = ["Choose a valid drawn section."];
        if (!double.IsFinite(request.DistanceFlexibilityPercent) || request.DistanceFlexibilityPercent != 0 &&
            request.DistanceFlexibilityPercent is < 5 or > 30)
            errors["distanceFlexibilityPercent"] = ["Choose 0 for normal distance matching, or a margin between 5% and 30%."];
        if (errors.Count == 0 && request.End is { } end && request.RadiusKm is { } r && Geo.DistanceKm(request.Start!, end) > r)
            errors["end"] = ["The end location is outside the radius from the requested start."];
        return errors;
    }
}
