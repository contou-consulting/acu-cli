using System.Text.Json;

namespace AcuCli.Core;

/// <summary>A response from the Acumatica API.</summary>
public sealed class AcuResponse
{
    /// <summary>HTTP status code.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Response media type, if any.</summary>
    public string? ContentType { get; init; }

    /// <summary>The raw response body.</summary>
    public string? Raw { get; init; }

    /// <summary>The parsed JSON body, or null when the body is empty or not JSON.</summary>
    public JsonElement? Json { get; init; }

    internal static async Task<AcuResponse> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? raw = null;
        if (response.Content is not null)
            raw = await response.Content.ReadAsStringAsync(ct);

        JsonElement? json = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                json = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Non-JSON body (HTML error page, EDMX XML, ...); leave Json null.
            }
        }

        return new AcuResponse
        {
            StatusCode = (int)response.StatusCode,
            ContentType = response.Content?.Headers.ContentType?.MediaType,
            Raw = raw,
            Json = json,
        };
    }
}
