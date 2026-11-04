using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meander.Core;
using Microsoft.AspNetCore.WebUtilities;

namespace Meander.Infrastructure;

public sealed class GarminConnector(HttpClient http, GarminOptions options, TimeProvider clock) : IServiceConnector
{
    public ServiceKind Service => ServiceKind.Garmin;
    public bool IsConfigured => options.IsConfigured;

    public Uri BuildAuthorizeUrl(string state, string codeChallenge, Uri redirectUri) => new(QueryHelpers.AddQueryString(options.AuthorizeUrl,
        new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = options.ClientId, ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256", ["redirect_uri"] = redirectUri.ToString(), ["state"] = state
        }));

    public async Task<ServiceTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct)
    {
        var tokens = await TokenAsync(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = codeVerifier, ["redirect_uri"] = redirectUri.ToString()
        }, null, ct);
        return tokens with { ExternalUserId = await UserIdAsync(tokens.AccessToken, ct) };
    }

    public Task<ServiceTokens> RefreshAsync(ServiceTokens tokens, CancellationToken ct) => tokens.RefreshToken is { } refresh
        ? TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }, tokens.ExternalUserId, ct)
        : throw new ProviderException(ProviderFailure.Unauthorized, "Garmin connection has no refresh token.");

    public async Task RevokeAsync(ServiceTokens tokens, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, options.RegistrationUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Unauthorized)
            throw new ProviderException(ProviderFailure.Unavailable, $"Garmin revoke failed ({(int)response.StatusCode}).");
    }

    private async Task<ServiceTokens> TokenAsync(Dictionary<string, string> form, string? userId, CancellationToken ct)
    {
        form["client_id"] = options.ClientId;
        form["client_secret"] = options.ClientSecret;
        using var response = await http.PostAsync(options.TokenUrl, new FormUrlEncodedContent(form), ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            throw new ProviderException(ProviderFailure.Unauthorized, "Garmin rejected the authorization.");
        if (!response.IsSuccessStatusCode)
            throw new ProviderException(ProviderFailure.Unavailable, $"Garmin token endpoint failed ({(int)response.StatusCode}).");
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        if (string.IsNullOrEmpty(body?.AccessToken))
            throw new ProviderException(ProviderFailure.InvalidResponse, "Garmin returned no access token.");
        return new ServiceTokens(body.AccessToken, body.RefreshToken, clock.GetUtcNow().AddSeconds(Math.Max(0, body.ExpiresIn)), userId);
    }

    private async Task<string> UserIdAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.UserIdUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw NoUserId();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var id = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("userId", out var value)
            ? value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null } : null;
        return string.IsNullOrEmpty(id) ? throw NoUserId() : id;
    }

    private static ProviderException NoUserId() => new(ProviderFailure.InvalidResponse, "Garmin returned no user id.");
    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] long ExpiresIn);
}
