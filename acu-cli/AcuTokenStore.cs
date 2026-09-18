using System.Text.Json;
using System.Text.Json.Serialization;
using AcuCli.Core;

namespace acu_cli;

/// <summary>
/// One OAuth session (for a single site). The client secret is stored (with a warning at
/// login time) so tokens can be renewed non-interactively.
/// </summary>
internal sealed class AcuAuthSession
{
    public string? Site { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? Grant { get; set; } // "authorization-code" | "client-credentials"
    public string? Scope { get; set; }
    public string? TokenType { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? RevocationEndpoint { get; set; }

    [JsonIgnore]
    public bool IsUsable => !string.IsNullOrWhiteSpace(AccessToken)
        && ExpiresAtUtc - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60);
}

/// <summary>
/// The persisted OAuth sessions, stored at ~/.acu-cli/auth.json (chmod 600) as one entry
/// per site, so logins to different sites (e.g. a mock and a production instance) never
/// clobber each other.
/// </summary>
internal static class AcuTokenStore
{
    public static string FilePath { get; } = Path.Combine(AcuConfig.DirectoryPath, "auth.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static Dictionary<string, AcuAuthSession> LoadAll()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;

            // Current format: { "sessions": { "<site-url>": {...} } }
            if (root.TryGetProperty("sessions", out var sessions)
                && sessions.ValueKind == JsonValueKind.Object)
            {
                return sessions.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Object)
                    .ToDictionary(
                        p => p.Name,
                        p => JsonSerializer.Deserialize<AcuAuthSession>(p.Value.GetRawText(), JsonOptions) ?? new AcuAuthSession(),
                    StringComparer.OrdinalIgnoreCase);
            }

            // Legacy format: a single session at the top level.
            var legacy = JsonSerializer.Deserialize<AcuAuthSession>(root.GetRawText(), JsonOptions);
            return legacy?.Site is not null
                ? new Dictionary<string, AcuAuthSession>(StringComparer.OrdinalIgnoreCase)
                    { [legacy.Site] = legacy }
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new CliException($"Could not read token file {FilePath}: {ex.Message}");
        }
    }

    private static void SaveAll(Dictionary<string, AcuAuthSession> sessions)
    {
        try
        {
            Directory.CreateDirectory(AcuConfig.DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new { sessions }, JsonOptions));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"Could not write token file {FilePath}: {ex.Message}");
        }
    }

    private static string KeyFor(string siteUrl) => siteUrl.Trim().TrimEnd('/');

    public static AcuAuthSession? FindForSite(string siteUrl)
    {
        var sessions = LoadAll();
        return sessions.TryGetValue(KeyFor(siteUrl), out var session) ? session : null;
    }

    public static void SaveSession(AcuAuthSession session)
    {
        if (string.IsNullOrWhiteSpace(session.Site))
            throw new CliException("A session must record its site URL.");
        var sessions = LoadAll();
        sessions[KeyFor(session.Site)] = session;
        SaveAll(sessions);
    }

    /// <summary>Removes the site's session; returns true when one existed.</summary>
    public static bool DeleteSession(string siteUrl)
    {
        var sessions = LoadAll();
        if (!sessions.Remove(KeyFor(siteUrl)))
            return false;
        if (sessions.Count == 0)
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        else
        {
            SaveAll(sessions);
        }
        return true;
    }

    public static IEnumerable<AcuAuthSession> All() => LoadAll().Values;

    /// <summary>
    /// Returns a usable session for the site, renewing it when needed:
    /// client-credentials sessions are silently re-requested, authorization-code sessions
    /// use the refresh token. Throws a CliException when no session exists or renewal fails.
    /// </summary>
    public static async Task<AcuAuthSession> EnsureValidTokenAsync(
        string siteUrl, bool ignoreCertificateErrors, CancellationToken ct)
    {
        var auth = FindForSite(siteUrl)
            ?? throw new CliException(
                $"Not logged in to {siteUrl}. Run `acu login` first (OAuth is required for API access).");

        if (auth.IsUsable)
            return auth;

        if (string.IsNullOrWhiteSpace(auth.ClientId) || string.IsNullOrWhiteSpace(auth.TokenEndpoint))
            throw new CliException("The stored OAuth session is incomplete; run `acu login` again.");

        using var oidc = new AcuOidcClient(ignoreCertificateErrors);
        if (string.Equals(auth.Grant, "client-credentials", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(auth.ClientSecret))
                throw new CliException(
                    "The access token expired and no client secret was stored; run `acu login --client-secret ...` again.");

            try
            {
                var tokens = await oidc.ClientCredentialsAsync(
                    auth.TokenEndpoint, auth.ClientId, auth.ClientSecret, auth.Scope ?? "api", ct);
                Apply(auth, tokens);
            }
            catch (AcuApiException ex)
            {
                throw new CliException($"Renewing the client-credentials token failed ({ex.Message}); run `acu login` again.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(auth.RefreshToken))
        {
            try
            {
                var tokens = await oidc.RefreshAsync(
                    auth.TokenEndpoint, auth.ClientId, auth.ClientSecret, auth.RefreshToken, ct);
                Apply(auth, tokens);
            }
            catch (AcuApiException ex)
            {
                throw new CliException($"The OAuth session could not be refreshed ({ex.Message}); run `acu login` again.");
            }
        }
        else
        {
            throw new CliException(
                "The access token expired and no refresh token was stored; run `acu login` again.");
        }

        SaveSession(auth);
        return auth;
    }

    private static void Apply(AcuAuthSession auth, AcuTokenSet tokens)
    {
        auth.AccessToken = tokens.AccessToken;
        auth.RefreshToken = tokens.RefreshToken ?? auth.RefreshToken;
        auth.TokenType = tokens.TokenType;
        auth.ExpiresAtUtc = tokens.ExpiresAtUtc;
        if (tokens.Scope is not null)
            auth.Scope = tokens.Scope;
    }
}
