using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Meander.Core;
using Meander.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables();

// Bind after Build so host-added configuration (including integration-test hosts)
// participates before validation and before any provider services are resolved.
var ors = new OrsOptions();
var generation = new GenerationOptions();
var limits = new ApiRateOptions();
var storage = new ResultStorageOptions();
var woodland = new WoodlandOptions();
var geocoding = new GeocodingOptions();
var photon = new PhotonOptions();
var mapData = new MapDataOptions();

builder.Services.AddSingleton(ors);
builder.Services.AddSingleton(photon);
builder.Services.AddSingleton(generation);
builder.Services.AddSingleton(storage);
builder.Services.AddSingleton(woodland);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ActivityProfiles>();
builder.Services.AddSingleton<ProviderBudget>();
builder.Services.AddTransient<ProviderBudgetHandler>();
builder.Services.AddSingleton<ICandidateStore, CandidateStore>();
builder.Services.AddSingleton<IRouteSearchStore, SqliteRouteSearchStore>();
builder.Services.AddTransient<RouteGenerator>();
builder.Services.AddTransient<RouteEditor>();
builder.Services.AddHttpClient("woodland", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Meander/1.0");
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.MaxResponseContentBufferSize = woodland.MaxResponseMegabytes * 1024L * 1024;
});
builder.Services.AddSingleton<IWoodlandProvider, OverpassWoodlandProvider>();
AddProviderResilience("ors", builder.Services.AddHttpClient<OrsProvider>(client =>
{
    client.BaseAddress = new Uri(ors.BaseUrl.TrimEnd('/') + "/");
    // ORS expects the raw API key, which may contain base64 padding. It is
    // not an HTTP authentication scheme; the typed Authorization parser rejects it.
    // Self-hosted ORS needs no key, so the header is omitted when none is configured.
    if (!string.IsNullOrWhiteSpace(ors.ApiKey))
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", ors.ApiKey);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Meander/1.0");
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
}));
AddProviderResilience("photon", builder.Services.AddHttpClient<PhotonGeocodingProvider>(client =>
{
    client.BaseAddress = new Uri(photon.BaseUrl.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Meander/1.0");
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
}));
builder.Services.AddTransient<IRoutingProvider>(sp => sp.GetRequiredService<OrsProvider>());
builder.Services.AddTransient<IGeocodingProvider>(sp => geocoding.Provider == GeocodingProviderKind.Photon
    ? sp.GetRequiredService<PhotonGeocodingProvider>() : sp.GetRequiredService<OrsProvider>());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.OnRejected = async (context, ct) =>
    {
        var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? Math.Ceiling(wait.TotalSeconds) : 5;
        context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, retry).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Results.Problem(statusCode: 429, title: "Too many requests", detail: "Please wait before trying again.")
            .ExecuteAsync(context.HttpContext);
    };
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.Equals("/api/routes/generate", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.Equals("/api/routes/preview", StringComparison.OrdinalIgnoreCase)
            ? RateLimitPartition.GetConcurrencyLimiter("generation", _ => new ConcurrencyLimiterOptions
                { PermitLimit = limits.ConcurrentGenerations, QueueLimit = 0 })
            : RateLimitPartition.GetNoLimiter("other"));
    o.AddPolicy("generate", context => RateLimitPartition.GetFixedWindowLimiter(ClientKey(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.GeneratePerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    o.AddPolicy("geocode", context => RateLimitPartition.GetFixedWindowLimiter(ClientKey(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.GeocodePerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    o.AddPolicy("preview", context => RateLimitPartition.GetFixedWindowLimiter(ClientKey(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.PreviewPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});

var app = builder.Build();
builder.Configuration.GetSection("Ors").Bind(ors);
builder.Configuration.GetSection("Generation").Bind(generation);
builder.Configuration.GetSection("RateLimiting").Bind(limits);
builder.Configuration.GetSection("ResultStorage").Bind(storage);
builder.Configuration.GetSection("Woodland").Bind(woodland);
builder.Configuration.GetSection("Geocoding").Bind(geocoding);
builder.Configuration.GetSection("Photon").Bind(photon);
builder.Configuration.GetSection("MapData").Bind(mapData);
if (geocoding.Provider == GeocodingProviderKind.Photon &&
    (!Uri.TryCreate(photon.BaseUrl, UriKind.Absolute, out var photonUrl) || photonUrl.Scheme is not ("http" or "https")))
    throw new InvalidOperationException("Photon:BaseUrl must be an absolute HTTP or HTTPS URL when Geocoding:Provider is Photon.");
string? mapDataPath = null;
if (!string.IsNullOrWhiteSpace(mapData.Path))
{
    mapDataPath = Path.GetFullPath(mapData.Path, app.Environment.ContentRootPath);
    if (!Directory.Exists(mapDataPath))
        throw new InvalidOperationException($"MapData:Path '{mapDataPath}' does not exist. Run scripts/fetch-map-data first, or leave MapData:Path empty.");
}
if (!Uri.TryCreate(woodland.Url, UriKind.Absolute, out var woodlandUrl) || woodlandUrl.Scheme is not ("http" or "https") ||
    !double.IsFinite(woodland.MaxSearchRadiusKm) || woodland.MaxSearchRadiusKm is < 1 or > 20 ||
    woodland.TimeoutSeconds is < 3 or > 20 || woodland.CacheHours is < 1 or > 168 || woodland.MaxResponseMegabytes is < 1 or > 128)
    throw new InvalidOperationException("Invalid Woodland configuration. Check URL, radius (1–20 km), timeout (3–20 s), cache hours (1–168), and maximum response size (1–128 MB).");
if (storage.DatabasePath != ":memory:")
    storage.DatabasePath = Path.GetFullPath(storage.DatabasePath, app.Environment.ContentRootPath);
ConfigurationChecks.Validate(ors, generation);
// Fail at startup if the database location is not writable, before spending routing quota.
app.Services.GetRequiredService<IRouteSearchStore>();
if (limits.GeneratePerMinute < 1 || limits.GeocodePerMinute < 1 || limits.PreviewPerMinute < 1 || limits.ConcurrentGenerations < 1)
    throw new InvalidOperationException("RateLimiting values must be positive integers.");
app.UseExceptionHandler();
app.UseStatusCodePages(async context =>
{
    var status = context.HttpContext.Response.StatusCode;
    await Results.Problem(statusCode: status, title: status == 404 ? "Not found" : "Request failed")
        .ExecuteAsync(context.HttpContext);
});
var hasWebRoot = Directory.Exists(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"));
if (hasWebRoot)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
if (mapDataPath is not null)
{
    // Local basemap: a PMTiles archive plus glyphs and sprites. PMTiles reads it with HTTP range requests.
    var mapTypes = new FileExtensionContentTypeProvider();
    mapTypes.Mappings[".pmtiles"] = "application/octet-stream";
    mapTypes.Mappings[".pbf"] = "application/x-protobuf";
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(mapDataPath), RequestPath = "/map", ContentTypeProvider = mapTypes
    });
}
// Route after static files so the fallback endpoints below only see requests no file matched.
app.UseRouting();
app.UseRateLimiter();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/routes/generate", async (RouteRequest request, RouteGenerator generator,
    IRouteSearchStore searches, ICandidateStore cache, HttpContext context, CancellationToken ct) =>
{
    // Capture the prefix before generation so cache eviction during a long search
    // cannot leave the saved search without its drawing.
    var draft = request.DrawnRouteId is { } draftId ? cache.GetDraft(draftId) : null;
    var preview = request.DrawnRouteId is { } previewId ? cache.Get(previewId) : null;
    var candidates = await generator.GenerateAsync(request, ct);
    var saved = searches.Save(request, candidates, draft, preview);
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(saved.Candidates);
}).RequireRateLimiting("generate");
app.MapGet("/api/searches/{id:guid}", (Guid id, IRouteSearchStore searches, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return searches.Get(id) is { } search ? Results.Ok(search)
        : Results.Problem(statusCode: 404, title: "Saved routes not found", detail: "This saved search is unavailable. Check the link or generate new routes.");
});
app.MapPost("/api/routes/preview", async (RoutePreviewRequest request, RouteGenerator generator, CancellationToken ct) =>
    Results.Ok(await generator.PreviewAsync(request, ct))).RequireRateLimiting("preview");
app.MapPost("/api/routes/{id:guid}/edit", (Guid id, IRouteSearchStore searches, RouteEditor editor, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var candidate = searches.GetCandidate(id);
    var source = candidate?.SearchId is { } searchId ? searches.Get(searchId) : null;
    return candidate is not null && source is not null ? Results.Ok(editor.Begin(candidate, source.Request))
        : Results.Problem(statusCode: 404, title: "Route not found", detail: "This route is unavailable. Open a saved route or generate a new one.");
}).RequireRateLimiting("preview");
app.MapPost("/api/routes/save", (RouteRequest request, RouteEditor editor, IRouteSearchStore searches, ICandidateStore cache, HttpContext context) =>
{
    var candidate = editor.Save(request);
    var saved = searches.Save(request with { IsEditedRoute = true }, [candidate], cache.GetDraft(request.DrawnRouteId!.Value), cache.Get(request.DrawnRouteId.Value));
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(saved.Candidates);
}).RequireRateLimiting("preview");
app.MapGet("/api/geocode", async (string? q, IGeocodingProvider geocoder, CancellationToken ct) =>
{
    var text = q?.Trim() ?? "";
    if (text.Length is < 3 or > 200)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["Search must contain 3–200 characters."] });
    return Results.Ok(await geocoder.SearchAsync(text, ct));
}).RequireRateLimiting("geocode");
app.MapGet("/api/routes/{id:guid}/gpx", (Guid id, ICandidateStore store, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return store.Get(id) is { } candidate
        ? Results.File(GpxWriter.Write(candidate), "application/gpx+xml", $"meander-{candidate.DistanceKm:F1}km-{id:N}.gpx")
        : Results.Problem(statusCode: 404, title: "Route export expired or unavailable", detail: "Generate your route again to create a new GPX download.");
});
// API misses must remain ProblemDetails even when the production SPA is present.
app.MapFallback("/api/{**path}", () => Results.Problem(statusCode: 404, title: "API endpoint not found"));
app.MapFallback("/map/{**path}", () => Results.Problem(statusCode: 404, title: "Map file not found"));
if (hasWebRoot) app.MapFallbackToFile("index.html");
app.Run();

static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "local";

void AddProviderResilience(string name, IHttpClientBuilder client)
{
    client.AddResilienceHandler(name, pipeline =>
    {
        pipeline.AddTimeout(TimeSpan.FromSeconds(30));
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = Math.Max(1, ors.RetryCount),
            ShouldHandle = args => ValueTask.FromResult(ors.RetryCount > 0 &&
                (args.Outcome.Exception is HttpRequestException or TimeoutRejectedException ||
                 args.Outcome.Result is { } response && ((int)response.StatusCode >= 500 || (int)response.StatusCode is 408 or 429))),
            Delay = TimeSpan.FromSeconds(.5), BackoffType = DelayBackoffType.Exponential, UseJitter = true,
            ShouldRetryAfterHeader = true
        });
        pipeline.AddTimeout(TimeSpan.FromSeconds(ors.AttemptTimeoutSeconds));
    });
    client.AddHttpMessageHandler<ProviderBudgetHandler>();
}
public partial class Program;

public sealed class ApiRateOptions
{
    public int GeneratePerMinute { get; set; } = 3;
    public int ConcurrentGenerations { get; set; } = 2;
    public int GeocodePerMinute { get; set; } = 60;
    public int PreviewPerMinute { get; set; } = 30;
}

public static class ConfigurationChecks
{
    public static void Validate(OrsOptions ors, GenerationOptions generation)
    {
        if (!Uri.TryCreate(ors.BaseUrl, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || uri.Scheme == "http"))
            throw new InvalidOperationException("Ors:BaseUrl must be an absolute HTTP or HTTPS URL.");
        // Only the hosted API needs a key; a self-hosted ORS (see compose.local.yaml) runs without one.
        if (ors.IsHostedApi && (string.IsNullOrWhiteSpace(ors.ApiKey) || ors.ApiKey.Contains("your-", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Set Ors__ApiKey in the environment or Ors:ApiKey in ignored appsettings.Local.json. Get a key from https://openrouteservice.org/, or point Ors:BaseUrl at a self-hosted ORS.");
        if (ors.ApiKey.Any(char.IsControl))
            throw new InvalidOperationException("Ors:ApiKey must be a single line without control characters.");
        if (ors.Concurrency is < 1 or > 20 || ors.AttemptTimeoutSeconds is < 1 or > 25 || ors.RetryCount is < 0 or > 5 ||
            ors.DirectionsPerMinute < 1 || ors.GeocodingPerMinute < 1 || ors.SnapsPerMinute < 1)
            throw new InvalidOperationException("Invalid Ors limits. Use concurrency 1–20, timeout 1–25 seconds, retries 0–5, and positive request budgets.");
        if (!double.IsFinite(generation.MaxDistanceKm) || generation.MaxDistanceKm < .5 || generation.MaxDistanceKm > 100 ||
            !double.IsFinite(generation.SnapRadiusM) || generation.SnapRadiusM <= 0 || generation.SnapRadiusM > 10000 ||
            !double.IsFinite(generation.SnapWarningM) || generation.SnapWarningM < 0 ||
            !double.IsFinite(generation.DistanceTolerance) || generation.DistanceTolerance <= 0 || generation.DistanceTolerance > .5 ||
            generation.DeadlineSeconds is < 1 or > 120 || generation.CacheTtlMinutes is < 1 or > 1440)
            throw new InvalidOperationException("Invalid Generation configuration. Check distance, snapping, tolerance, deadline, and cache TTL settings.");
    }
}

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return true;
        var (status, title) = exception switch
        {
            RouteValidationException => (422, "Cannot generate this route"),
            BadHttpRequestException => (400, "Invalid request"),
            ProviderException { Failure: ProviderFailure.NoRoute } => (422, "No routable path found"),
            ProviderException { Failure: ProviderFailure.Timeout } or TimeoutRejectedException or OperationCanceledException => (504, "Routing service timed out"),
            ProviderException { Failure: ProviderFailure.InvalidResponse } => (502, "Invalid routing service response"),
            ProviderException or HttpRequestException => (503, "Routing service unavailable"),
            _ => (500, "Unexpected server error")
        };
        if (exception is ProviderException { Failure: ProviderFailure.Throttled }) context.Response.Headers.RetryAfter = "60";
        if (status == 500) logger.LogError(exception, "Unhandled request failure {TraceId}", context.TraceIdentifier);
        else logger.LogWarning("Request failed: {Status} {FailureType}, trace {TraceId}", status, exception.GetType().Name, context.TraceIdentifier);
        if (exception is ProviderException { Failure: ProviderFailure.InvalidResponse, InnerException: { } cause } invalid)
            logger.LogWarning(cause, "ORS {ResponseSection} response could not be parsed, trace {TraceId}", invalid.ResponseSection ?? "unknown", context.TraceIdentifier);
        var problem = new ProblemDetails
        {
            Status = status, Title = title,
            Detail = exception is RouteValidationException or ProviderException ? exception.Message
                : status == 400 ? "The request body or query is malformed. Check the API contract."
                : "Please try again shortly.",
            Instance = context.Request.Path
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;
        if (exception is ProviderException { ResponseSection: { } section }) problem.Extensions["providerStage"] = section;
        if (exception is RouteValidationException validation)
        {
            if (validation.Errors.Count > 0) problem.Extensions["errors"] = validation.Errors;
            if (validation.MinimumDistanceKm is { } minimum) problem.Extensions["minimumDistanceKm"] = minimum;
        }
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken);
        return true;
    }
}
