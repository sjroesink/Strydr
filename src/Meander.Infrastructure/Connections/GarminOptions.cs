namespace Meander.Infrastructure;

// Garmin Connect Developer Program (OAuth 2.0 PKCE + Courses API). Every URL is configurable
// so a change on Garmin's side is a configuration change. Empty ClientId = integration off.
public sealed class GarminOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AuthorizeUrl { get; set; } = "https://connect.garmin.com/oauth2Confirm";
    public string TokenUrl { get; set; } = "https://connectapi.garmin.com/di-oauth2-service/oauth/token";
    public string UserIdUrl { get; set; } = "https://apis.garmin.com/wellness-api/rest/user/id";
    public string RegistrationUrl { get; set; } = "https://apis.garmin.com/wellness-api/rest/user/registration";
    public string CourseUrl { get; set; } = "https://apis.garmin.com/training-api/courses/v1/course";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public void Validate()
    {
        if (!IsConfigured) return;
        if (string.IsNullOrWhiteSpace(ClientSecret))
            throw new InvalidOperationException("Set Integrations__Garmin__ClientSecret together with the client id.");
        foreach (var url in new[] { AuthorizeUrl, TokenUrl, UserIdUrl, RegistrationUrl, CourseUrl })
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException($"Integrations:Garmin URL '{url}' must be an absolute HTTP or HTTPS URL.");
    }
}
