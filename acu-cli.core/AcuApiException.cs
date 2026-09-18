using System.Text.Json;

namespace AcuCli.Core;

/// <summary>Thrown when the Acumatica API (or this wrapper's configuration) reports an error.</summary>
public sealed class AcuApiException : Exception
{
    public int? StatusCode { get; }
    public string? ResponseBody { get; }

    public AcuApiException(string message)
        : base(message)
    {
    }

    public AcuApiException(int statusCode, string? responseBody, string? reasonPhrase)
        : base(ComposeMessage(statusCode, responseBody, reasonPhrase))
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    private static string ComposeMessage(int statusCode, string? body, string? reasonPhrase)
    {
        var text = $"HTTP {statusCode} {reasonPhrase}".Trim();
        var detail = AcuErrorParser.ExtractMessage(body);
        if (!string.IsNullOrEmpty(detail))
            text += $": {detail}";
        else if (!string.IsNullOrWhiteSpace(body))
            text += $": {Truncate(body.Trim(), 500)}";
        return text;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}

/// <summary>Thrown when required connection settings are missing or invalid.</summary>
public sealed class AcuConfigurationException : Exception
{
    public AcuConfigurationException(string message) : base(message)
    {
    }
}

/// <summary>Best-effort extraction of a human-readable message from an Acumatica error body.</summary>
internal static class AcuErrorParser
{
    public static string? ExtractMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return ExtractFromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractFromElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        // Real-site format: { "message": "An error has occurred.",
        //   "exceptionMessage": "Error: Invalid credentials. Please try again.", ... }
        // The exception message carries the actual detail, so it wins.
        // OAuth error bodies use { "error": "invalid_client", "error_description": "..." }.
        foreach (var name in new[] { "exceptionMessage", "error_description", "message", "Message", "error" })
        {
            if (element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString();
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var nested = ExtractFromElement(value);
                    if (nested is not null)
                        return nested;
                }
            }
        }

        // OData-style errors: { "error": { "code": "...", "message": "..." } }
        if (element.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return message.GetString();
        }

        return null;
    }
}
