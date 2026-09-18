using System.Text.Json;
using AcuCli.Core;

namespace acu_cli;

/// <summary>
/// Caches endpoint schemas (swagger.json documents) under ~/.acu-cli/schema/{endpoint}/{version}.
/// Endpoint names are just names, so custom endpoints cache the same way as Default.
/// The index records which site a cache came from; a different site invalidates it, because
/// custom endpoints (and endpoint versions) differ per site.
/// </summary>
internal static class SchemaCache
{
    public sealed class Index
    {
        public required string Site { get; init; }
        public required string Endpoint { get; init; }
        public required string Version { get; init; }
        public required DateTime FetchedAt { get; init; }
        public required int EntityCount { get; init; }
    }

    public static string RootPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".acu-cli", "schema");

    public static string EndpointPath(string endpoint, string version) =>
        Path.Combine(RootPath, Sanitize(endpoint), Sanitize(version));

    public static string SwaggerPath(string endpoint, string version) =>
        Path.Combine(EndpointPath(endpoint, version), "swagger.json");

    private static string IndexPath(string endpoint, string version) =>
        Path.Combine(EndpointPath(endpoint, version), "index.json");

    private static readonly JsonSerializerOptions IndexJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Loads the cached schema for the given site/endpoint/version, or null when it is absent,
    /// unreadable, or was fetched from a different site.
    /// </summary>
    public static AcuEndpointSchema? TryLoad(string site, string endpoint, string version)
    {
        try
        {
            var swaggerPath = SwaggerPath(endpoint, version);
            if (!File.Exists(swaggerPath) || !File.Exists(IndexPath(endpoint, version)))
                return null;

            using var indexDoc = JsonDocument.Parse(File.ReadAllText(IndexPath(endpoint, version)));
            var indexSite = indexDoc.RootElement.TryGetProperty("site", out var siteElement)
                ? siteElement.GetString()
                : null;
            if (!string.Equals(indexSite, site, StringComparison.OrdinalIgnoreCase))
                return null;

            return AcuEndpointSchema.Parse(File.ReadAllText(swaggerPath), endpoint, version);
        }
        catch
        {
            return null;
        }
    }

    public static Index? ReadIndex(string endpoint, string version)
    {
        try
        {
            var path = IndexPath(endpoint, version);
            if (!File.Exists(path))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new Index
            {
                Site = root.GetProperty("site").GetString() ?? "",
                Endpoint = root.GetProperty("endpoint").GetString() ?? "",
                Version = root.GetProperty("version").GetString() ?? "",
                FetchedAt = root.GetProperty("fetchedAt").GetDateTime(),
                EntityCount = root.GetProperty("entityCount").GetInt32(),
            };
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string site, string endpoint, string version, string swaggerJson, AcuEndpointSchema schema)
    {
        Directory.CreateDirectory(EndpointPath(endpoint, version));
        File.WriteAllText(SwaggerPath(endpoint, version), swaggerJson);
        File.WriteAllText(IndexPath(endpoint, version), JsonSerializer.Serialize(new
        {
            site,
            endpoint,
            version,
            fetchedAt = DateTime.UtcNow,
            entityCount = schema.Entities.Count,
        }, IndexJsonOptions));
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
