using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AcuCli.Core;

/// <summary>An endpoint installed on the site, as reported by the public <c>GET /entity</c> index.</summary>
public sealed record AcuSiteEndpoint(string Name, string Version, string Href);

/// <summary>Site information from the public <c>GET /entity</c> index.</summary>
public sealed class AcuSiteInfo
{
    public required string AcumaticaBuildVersion { get; init; }
    public required string DatabaseVersion { get; init; }
    public required IReadOnlyList<AcuSiteEndpoint> Endpoints { get; init; }

    /// <summary>Returns the versions of the named endpoint, newest first (may be empty).</summary>
    public IReadOnlyList<string> VersionsOf(string endpointName) =>
        Endpoints
            .Where(e => string.Equals(e.Name, endpointName, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Version)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v, EndpointVersionComparer.Instance)
            .ToList();

    /// <summary>Returns the newest version of the named endpoint installed on the site, or null.</summary>
    public string? LatestVersion(string endpointName) => VersionsOf(endpointName).FirstOrDefault();

    /// <summary>Case-insensitive endpoint names installed on the site.</summary>
    public IEnumerable<string> EndpointNames() =>
        Endpoints.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    /// <summary>Sorts Acumatica versions like 25.200.001 descending by numeric parts.</summary>
    private sealed class EndpointVersionComparer : IComparer<string?>
    {
        public static readonly EndpointVersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null) return y is null ? 0 : -1;
            if (y is null) return 1;

            var xParts = x.Split('.');
            var yParts = y.Split('.');
            var count = Math.Max(xParts.Length, yParts.Length);
            for (var i = 0; i < count; i++)
            {
                var xValue = i < xParts.Length && int.TryParse(xParts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var xp) ? xp : 0;
                var yValue = i < yParts.Length && int.TryParse(yParts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var yp) ? yp : 0;
                if (xValue != yValue)
                    return xValue.CompareTo(yValue);
            }
            return string.CompareOrdinal(x, y);
        }
    }
}

/// <summary>
/// A client for Acumatica's contract-based REST API (default and custom endpoints).
/// Authenticates with an OAuth 2.0 bearer token; supports entity CRUD, actions, the
/// OpenAPI schema document, and the public site/endpoint index.
/// </summary>
public sealed class AcuClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private bool _disposed;

    public AcuClientOptions Options { get; }

    public AcuClient(AcuClientOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(options.Url))
            throw new AcuConfigurationException("A site URL is required (e.g. https://acumatica.contou.com).");
        if (!Uri.TryCreate(options.Url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AcuConfigurationException($"'{options.Url}' is not a valid http(s) URL.");
        }
        _baseUrl = options.Url.Trim().TrimEnd('/');

        _http = AcuHttp.Create(options.IgnoreCertificateErrors, TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds)));
    }

    // ---------------------------------------------------------------- site index (public)

    /// <summary>
    /// Fetches the public <c>GET /entity</c> index: the Acumatica build version and every
    /// endpoint (name + version) installed on the site. Requires no authentication.
    /// </summary>
    public async Task<AcuSiteInfo> GetSiteInfoAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/entity");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var endpoints = root.TryGetProperty("endpoints", out var endpointArray)
                && endpointArray.ValueKind == JsonValueKind.Array
                    ? endpointArray.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.Object)
                        .Select(item => new AcuSiteEndpoint(
                            item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                            item.TryGetProperty("version", out var version) ? version.GetString() ?? "" : "",
                            item.TryGetProperty("href", out var href) ? href.GetString() ?? "" : ""))
                        .ToList()
                    : [];

            var version = root.TryGetProperty("version", out var versionObject)
                && versionObject.ValueKind == JsonValueKind.Object
                    ? versionObject
                    : default;

            return new AcuSiteInfo
            {
                AcumaticaBuildVersion = version.ValueKind == JsonValueKind.Object
                    && version.TryGetProperty("acumaticaBuildVersion", out var build)
                        ? build.GetString() ?? ""
                        : "",
                DatabaseVersion = version.ValueKind == JsonValueKind.Object
                    && version.TryGetProperty("databaseVersion", out var database)
                        ? database.GetString() ?? ""
                        : "",
                Endpoints = endpoints,
            };
        }
        catch (JsonException ex)
        {
            throw new AcuApiException($"The site index at {_baseUrl}/entity is not valid JSON: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- entities

    /// <summary>
    /// Lists entities (<paramref name="keys"/> empty) or fetches a single record identified by
    /// its key values (composite keys are passed in order, one path segment each).
    /// </summary>
    public Task<AcuResponse> GetAsync(
        string entity,
        IReadOnlyList<string>? keys = null,
        AcuQuery? query = null,
        CancellationToken ct = default)
        => SendEntityAsync(HttpMethod.Get, entity, keys, query, body: null, ensureSuccess: true, ct);

    /// <summary>
    /// Inserts or updates a record (upsert). The key fields must be present in the JSON body;
    /// this API version exposes PUT only on the entity collection URL. An optional
    /// <paramref name="select"/> trims the returned record to specific fields ($select).
    /// </summary>
    public Task<AcuResponse> PutAsync(
        string entity,
        string jsonBody,
        string? select = null,
        CancellationToken ct = default)
        => SendEntityAsync(HttpMethod.Put, entity, keys: null, query: select is null ? null : new AcuQuery { Select = select }, body: jsonBody, ensureSuccess: true, ct);

    /// <summary>
    /// Updates only the fields specified in the JSON body of an existing record (identified by
    /// its key fields in the body). An optional <paramref name="select"/> trims the returned
    /// record ($select).
    /// </summary>
    public Task<AcuResponse> PatchAsync(
        string entity,
        string jsonBody,
        string? select = null,
        CancellationToken ct = default)
        => SendEntityAsync(HttpMethod.Patch, entity, keys: null, query: select is null ? null : new AcuQuery { Select = select }, body: jsonBody, ensureSuccess: true, ct);

    /// <summary>Deletes the record identified by <paramref name="keys"/>.</summary>
    public Task<AcuResponse> DeleteAsync(
        string entity,
        IReadOnlyList<string> keys,
        CancellationToken ct = default)
    {
        if (keys is null || keys.Count == 0)
            throw new AcuConfigurationException("At least one key value is required to delete a record.");
        return SendEntityAsync(HttpMethod.Delete, entity, keys, query: null, body: null, ensureSuccess: true, ct);
    }

    /// <summary>
    /// Invokes an action on an entity. Actions that require a record receive the record's
    /// key fields in the body as an <c>entity</c> object; simple actions receive their
    /// parameters directly.
    /// </summary>
    public Task<AcuResponse> InvokeActionAsync(
        string entity,
        string action,
        string? jsonBody = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entity) || string.IsNullOrWhiteSpace(action))
            throw new AcuConfigurationException("An entity name and action name are required.");
        return SendEntityAsync(
            HttpMethod.Post, entity, keys: new[] { action }, query: null,
            body: jsonBody ?? "{}", ensureSuccess: true, ct);
    }

    // ---------------------------------------------------------------- schema (public)

    /// <summary>
    /// Retrieves the OpenAPI (swagger.json) document for the configured endpoint and version.
    /// This does not require authentication and doubles as a validity check for the
    /// endpoint/version combination (a 404 means they don't exist on the site).
    /// </summary>
    public async Task<string> GetSwaggerAsync(CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/entity/{Uri.EscapeDataString(Options.EndpointName)}/{Uri.EscapeDataString(RequireVersion())}/swagger.json";
        using var response = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    // ---------------------------------------------------------------- escape hatch

    /// <summary>
    /// Sends an arbitrary request against the site with the current bearer token.
    /// <paramref name="pathAndQuery"/> is either absolute or relative to the site root
    /// (e.g. <c>entity/Default/25.200.001/StockItem?$top=1</c>).
    /// </summary>
    public async Task<AcuResponse> SendAsync(
        string method,
        string pathAndQuery,
        string? jsonBody = null,
        bool ensureSuccess = false,
        CancellationToken ct = default)
    {
        var url = pathAndQuery.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = $"{_baseUrl}/{url.TrimStart('/')}";

        using var request = new HttpRequestMessage(new HttpMethod(method.Trim().ToUpperInvariant()), url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (Options.AccessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.AccessToken);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        if (ensureSuccess)
            await EnsureSuccessAsync(response, ct);
        return await AcuResponse.ReadAsync(response, ct);
    }

    // ---------------------------------------------------------------- internals

    private async Task<AcuResponse> SendEntityAsync(
        HttpMethod method,
        string entity,
        IReadOnlyList<string>? keys,
        AcuQuery? query,
        string? body,
        bool ensureSuccess,
        CancellationToken ct)
    {
        var url = BuildEntityUrl(entity, keys, query);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (Options.AccessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.AccessToken);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        if (ensureSuccess)
            await EnsureSuccessAsync(response, ct);
        return await AcuResponse.ReadAsync(response, ct);
    }

    private string RequireVersion()
    {
        if (string.IsNullOrWhiteSpace(Options.EndpointVersion))
            throw new AcuConfigurationException(
                "No endpoint version is configured. Pass --endpoint-version (e.g. 25.200.001), set ACU_VERSION, " +
                "or run `acu login`.");
        return Options.EndpointVersion;
    }

    /// <summary>
    /// Base fields present on every entity. They are always returned by the API but rejected
    /// by <c>$select</c> (the server answers HTTP 500), so they are stripped from the list.
    /// </summary>
    private static readonly HashSet<string> BaseFields = new(StringComparer.OrdinalIgnoreCase)
        { "id", "rowNumber", "note", "custom", "error", "files", "_links" };

    private static string? StripBaseFields(string? select)
    {
        if (string.IsNullOrWhiteSpace(select))
            return select;
        var kept = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(field => !BaseFields.Contains(field))
            .ToList();
        return kept.Count == 0 ? null : string.Join(",", kept);
    }

    private string BuildEntityUrl(string entity, IReadOnlyList<string>? keys, AcuQuery? query)
    {
        if (string.IsNullOrWhiteSpace(entity))
            throw new AcuConfigurationException("An entity name is required (e.g. StockItem).");

        var sb = new StringBuilder(_baseUrl)
            .Append("/entity/")
            .Append(Uri.EscapeDataString(Options.EndpointName))
            .Append('/')
            .Append(Uri.EscapeDataString(RequireVersion()))
            .Append('/')
            .Append(Uri.EscapeDataString(entity.Trim()));

        if (keys is not null)
        {
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                    throw new AcuConfigurationException("Key values must not be empty.");
                sb.Append('/').Append(Uri.EscapeDataString(key.Trim()));
            }
        }

        var hasQuery = false;
        void AddParam(string name, string? value, bool escape)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            sb.Append(hasQuery ? '&' : '?');
            hasQuery = true;
            sb.Append(name).Append('=');
            sb.Append(escape ? Uri.EscapeDataString(value) : value);
        }

        if (query is not null)
        {
            AddParam("$filter", query.Filter, escape: true);
            AddParam("$select", StripBaseFields(query.Select), escape: false);
            AddParam("$expand", query.Expand, escape: false);
            AddParam("$custom", query.Custom, escape: false);
            if (query.Top is int top and > 0)
                AddParam("$top", top.ToString(CultureInfo.InvariantCulture), escape: false);
            if (query.Skip is int skip and > 0)
                AddParam("$skip", skip.ToString(CultureInfo.InvariantCulture), escape: false);
        }

        return sb.ToString();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await SafeReadBodyAsync(response, ct);
        throw new AcuApiException((int)response.StatusCode, body, response.ReasonPhrase);
    }

    private static async Task<string?> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (response.Content is null)
                return null;
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return null;
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
