namespace acu_cli;

/// <summary>Input helpers: request bodies from file/stdin.</summary>
internal static class Input
{
    /// <summary>
    /// Reads a body from <paramref name="filePath"/> when given, otherwise from stdin when
    /// piped. Returns null when neither is available or the input is empty.
    /// </summary>
    public static async Task<string?> TryReadStdinOrFileAsync(string? filePath)
    {
        if (filePath is not null)
        {
            if (!File.Exists(filePath))
                throw new CliException($"File not found: {filePath}");
            return await File.ReadAllTextAsync(filePath);
        }
        if (Console.IsInputRedirected)
            return await Console.In.ReadToEndAsync();
        return null;
    }

    /// <summary>Reads a required request body from a file or piped stdin.</summary>
    public static async Task<string> ReadRequiredBodyAsync(string? filePath)
    {
        var body = await TryReadStdinOrFileAsync(filePath);
        if (string.IsNullOrWhiteSpace(body))
            throw new CliException(
                filePath is null
                    ? "No request body provided. Use --file <path> or pipe JSON via stdin."
                    : $"The file {filePath} is empty.");
        return body;
    }
}
