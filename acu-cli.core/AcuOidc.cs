using System.Text.Json;

namespace AcuCli.Core;

/// <summary>An OAuth token set as returned by the identity provider's token endpoint.</summary>
public sealed record AcuTokenSet
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public string TokenType { get; init; } = "Bearer";
    public string? Scope { get; init; }
}

/// <summary>
/// The site's OpenID Connect configuration, discovered from
/// <c>{site}/identity/.well-known/openid-configuration</c>. No authentication required.
/// </summary>
public sealed class AcuOidcDiscovery
{
    public required string Issuer { get; init; }
    public required string AuthorizationEndpoint { get; init; }
    public required string TokenEndpoint { get; init; }
    public string? RevocationEndpoint { get; init; }
    public required IReadOnlyCollection<string> GrantTypesSupported { get; init; }
    public required IReadOnlyCollection<string> ScopesSupported { get; init; }

    public bool SupportsGrant(string grantType) => GrantTypesSupported.Contains(grantType);

    public static async Task<AcuOidcDiscovery> DiscoverAsync(
        string siteUrl, bool ignoreCertificateErrors, CancellationToken ct = default)
    {
        var url = siteUrl.Trim().TrimEnd('/') + "/identity/.well-known/openid-configuration";
        using var http = AcuHttp.Create(ignoreCertificateErrors);
        using var response = await http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if ((int)response.StatusCode == 404)
            throw new AcuApiException(
                $"OpenID Connect is not available on {siteUrl} (no discovery document at {url}).");
        if (!response.IsSuccessStatusCode)
            throw new AcuApiException((int)response.StatusCode, body, response.ReasonPhrase);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string Required(string name) => root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()!
                    : throw new AcuApiException($"The OIDC discovery document is missing '{name}'.");

            IReadOnlyCollection<string> OptionalList(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString() ?? "")
                        .Where(item => item.Length > 0)
                        .ToList()
                    : [];

            return new AcuOidcDiscovery
            {
                Issuer = Required("issuer"),
                AuthorizationEndpoint = Required("authorization_endpoint"),
                TokenEndpoint = Required("token_endpoint"),
                RevocationEndpoint = root.TryGetProperty("revocation_endpoint", out var revocation)
                    && revocation.ValueKind == JsonValueKind.String
                        ? revocation.GetString()
                        : null,
                GrantTypesSupported = OptionalList("grant_types_supported"),
                ScopesSupported = OptionalList("scopes_supported"),
            };
        }
        catch (JsonException ex)
        {
            throw new AcuApiException($"The OIDC discovery document at {url} is not valid JSON: {ex.Message}");
        }
    }
}

/// <summary>
/// Client for the OAuth 2.0 token operations of the site's identity provider: the
/// authorization-code exchange (with PKCE), the client-credentials grant, token refresh,
/// and token revocation.
/// </summary>
public sealed class AcuOidcClient : IDisposable
{
    private readonly HttpClient _http;
    private bool _disposed;

    public AcuOidcClient(bool ignoreCertificateErrors = false)
        => _http = AcuHttp.Create(ignoreCertificateErrors);

    public Task<AcuTokenSet> ExchangeAuthorizationCodeAsync(
        string tokenEndpoint, string clientId, string? clientSecret,
        string redirectUri, string code, string codeVerifier,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
        };
        if (clientSecret is not null)
            form["client_secret"] = clientSecret;
        return RequestTokensAsync(tokenEndpoint, form, ct);
    }

    public Task<AcuTokenSet> ClientCredentialsAsync(
        string tokenEndpoint, string clientId, string clientSecret, string scope,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        };
        if (!string.IsNullOrWhiteSpace(scope))
            form["scope"] = scope;
        return RequestTokensAsync(tokenEndpoint, form, ct);
    }

    public Task<AcuTokenSet> RefreshAsync(
        string tokenEndpoint, string clientId, string? clientSecret, string refreshToken,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        };
        if (clientSecret is not null)
            form["client_secret"] = clientSecret;
        return RequestTokensAsync(tokenEndpoint, form, ct);
    }

    /// <summary>Revokes a token at the revocation endpoint. Best effort: returns false on failure.</summary>
    public async Task<bool> RevokeAsync(
        string revocationEndpoint, string clientId, string? clientSecret, string token,
        CancellationToken ct = default)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["token"] = token,
                ["client_id"] = clientId,
            };
            if (clientSecret is not null)
                form["client_secret"] = clientSecret;
            using var content = new FormUrlEncodedContent(form);
            using var response = await _http.PostAsync(revocationEndpoint, content, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<AcuTokenSet> RequestTokensAsync(
        string tokenEndpoint, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(tokenEndpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AcuApiException((int)response.StatusCode, body, response.ReasonPhrase);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var accessToken)
                || accessToken.ValueKind != JsonValueKind.String)
            {
                throw new AcuApiException("The token response contained no access_token.");
            }

            var expiresInSeconds = root.TryGetProperty("expires_in", out var expiresIn)
                && expiresIn.ValueKind == JsonValueKind.Number
                && expiresIn.GetInt32() > 0
                    ? expiresIn.GetInt32()
                    : 3600;

            return new AcuTokenSet
            {
                AccessToken = accessToken.GetString()!,
                RefreshToken = root.TryGetProperty("refresh_token", out var refreshToken)
                    && refreshToken.ValueKind == JsonValueKind.String
                        ? refreshToken.GetString()
                        : null,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds),
                TokenType = root.TryGetProperty("token_type", out var tokenType)
                    && tokenType.ValueKind == JsonValueKind.String
                        ? tokenType.GetString() ?? "Bearer"
                        : "Bearer",
                Scope = root.TryGetProperty("scope", out var scope)
                    && scope.ValueKind == JsonValueKind.String
                        ? scope.GetString()
                        : null,
            };
        }
        catch (JsonException ex)
        {
            throw new AcuApiException($"The token response from {tokenEndpoint} is not valid JSON: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
    }
}
