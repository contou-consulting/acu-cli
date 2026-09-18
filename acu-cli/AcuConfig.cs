using System.Text.Json;

namespace acu_cli;

/// <summary>
/// Connection settings for ONE site (URL, endpoint, version, TLS). The file
/// ~/.acu-cli/config.json stores a profile per site plus which one is active, so
/// several sites can be signed in at once and switched with `acu use` — OAuth
/// sessions live separately in auth.json (AcuTokenStore), keyed by site URL.
/// </summary>
internal sealed class AcuConfig
{
    public string? Url { get; set; }
    public string? Endpoint { get; set; }
    public string? Version { get; set; }
    public bool Insecure { get; set; }

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".acu-cli");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record SiteFile(string? Active, Dictionary<string, AcuConfig> Sites);

    private static string Key(string url) => url.Trim().TrimEnd('/');

    // -------------------------------------------------- file access

    private static SiteFile LoadFile()
    {
        var empty = new SiteFile(null, new Dictionary<string, AcuConfig>(StringComparer.OrdinalIgnoreCase));
        try
        {
            if (!File.Exists(FilePath))
                return empty;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return empty;

            // Current format: { "active": "<url>", "sites": { "<url>": {profile} } }
            if (root.TryGetProperty("sites", out var sites) && sites.ValueKind == JsonValueKind.Object)
            {
                var parsed = new Dictionary<string, AcuConfig>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in sites.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object)
                        continue;
                    var profile = JsonSerializer.Deserialize<AcuConfig>(entry.Value.GetRawText(), JsonOptions) ?? new AcuConfig();
                    if (string.IsNullOrWhiteSpace(profile.Url))
                        profile.Url = entry.Name; // older writes may omit the url inside the profile
                    parsed[Key(entry.Name)] = profile;
                }
                string? active = null;
                if (root.TryGetProperty("active", out var activeEl)
                    && activeEl.ValueKind == JsonValueKind.String
                    && activeEl.GetString() is { } activeUrl)
                {
                    active = Key(activeUrl);
                }
                return new SiteFile(active, parsed);
            }

            // Legacy format: a single flat profile — the site it names becomes active.
            var legacy = JsonSerializer.Deserialize<AcuConfig>(root.GetRawText(), JsonOptions);
            if (!string.IsNullOrWhiteSpace(legacy?.Url))
            {
                var parsed = new Dictionary<string, AcuConfig>(StringComparer.OrdinalIgnoreCase)
                    { [Key(legacy.Url!)] = legacy };
                return new SiteFile(Key(legacy.Url!), parsed);
            }
            return empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new CliException($"Could not read config file {FilePath}: {ex.Message}");
        }
    }

    private static void SaveFile(SiteFile file)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new { active = file.Active, sites = file.Sites }, JsonOptions));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"Could not write config file {FilePath}: {ex.Message}");
        }
    }

    // -------------------------------------------------- profiles

    /// <summary>The profile of the active site (empty object when none is active).</summary>
    public static AcuConfig Load()
    {
        var file = LoadFile();
        if (file.Active is not null && file.Sites.TryGetValue(file.Active, out var profile))
            return profile;
        return new AcuConfig();
    }

    /// <summary>All stored site profiles (active first is NOT guaranteed; see ActiveUrl).</summary>
    public static IReadOnlyList<AcuConfig> Profiles() => LoadFile().Sites.Values.ToList();

    /// <summary>The active site URL, or null when nothing is active.</summary>
    public static string? ActiveUrl() => LoadFile().Active;

    /// <summary>Inserts or updates the site's profile and makes it the active site.</summary>
    public void Save() => SaveProfile(this);

    public static void SaveProfile(AcuConfig profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Url))
            throw new CliException("A site profile must record its URL.");
        var file = LoadFile();
        file.Sites[Key(profile.Url!)] = profile;
        SaveFile(file with { Active = Key(profile.Url!) });
    }

    /// <summary>Makes a stored profile the active site; false when it is not in the config.</summary>
    public static bool SetActive(string url)
    {
        var file = LoadFile();
        if (!file.Sites.ContainsKey(Key(url)))
            return false;
        SaveFile(file with { Active = Key(url) });
        return true;
    }

    /// <summary>
    /// Removes the site's profile. When the active site is removed, the first remaining
    /// site becomes active; the file is deleted when no sites are left.
    /// Returns true when a profile existed.
    /// </summary>
    public static bool DeleteProfile(string url)
    {
        var file = LoadFile();
        if (!file.Sites.Remove(Key(url)))
            return false;
        if (file.Sites.Count == 0)
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        else
        {
            var active = string.Equals(file.Active, Key(url), StringComparison.OrdinalIgnoreCase)
                ? file.Sites.Keys.First()
                : file.Active;
            SaveFile(file with { Active = active });
        }
        return true;
    }
}
