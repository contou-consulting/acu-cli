using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace acu_cli;

/// <summary>
/// Interactive OAuth 2.0 authorization-code flow with PKCE: generates the challenge pair,
/// starts a local listener on the redirect port, and captures the authorization code.
/// </summary>
internal static class AuthorizationCodeFlow
{
    /// <summary>Generates a PKCE verifier/challenge pair (RFC 7636, method S256).</summary>
    public static (string Verifier, string Challenge) CreatePkcePair()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    public static string BuildAuthorizeUrl(
        string authorizationEndpoint, string clientId, string redirectUri,
        string scope, string state, string codeChallenge)
    {
        var query = new List<(string, string)>
        {
            ("client_id", clientId),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("scope", scope),
            ("state", state),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"),
        };
        return authorizationEndpoint.TrimEnd('?')
            + (authorizationEndpoint.Contains('?') ? '&' : '?')
            + string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Item1)}={Uri.EscapeDataString(kv.Item2)}"));
    }

    /// <summary>
    /// Waits for the identity provider to redirect back with the authorization code.
    /// Serves one request on the redirect port and returns the code (validating state).
    /// </summary>
    public static async Task<string> ReceiveCodeAsync(
        Uri redirectUri, string expectedState, TimeSpan timeout, CancellationToken ct)
    {
        if (!string.Equals(redirectUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !Equals(redirectUri.Host, IPAddress.Loopback.ToString()))
        {
            throw new CliException($"The redirect URI must point at localhost (got {redirectUri.Host}).");
        }

        var listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
        listener.Start(1);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            while (true)
            {
                using var socket = await listener.AcceptSocketAsync(cts.Token);
                using var stream = new NetworkStream(socket, ownsSocket: false);

                var request = await ReadRequestHeaderAsync(stream, cts.Token);
                var target = ExtractRequestTarget(request);

                (string code, string state)? result = null;
                if (target is not null)
                {
                    var queryIndex = target.IndexOf('?');
                    if (queryIndex >= 0)
                    {
                        var parameters = ParseQueryString(target[(queryIndex + 1)..]);
                        parameters.TryGetValue("state", out var state);
                        parameters.TryGetValue("code", out var code);
                        if (!string.IsNullOrEmpty(code) && string.Equals(state, expectedState, StringComparison.Ordinal))
                            result = (code, state!);
                        if (!string.IsNullOrEmpty(parameters.GetValueOrDefault("error")))
                            throw new CliException(
                                $"The identity provider returned an error: {parameters.GetValueOrDefault("error")}" +
                                (parameters.TryGetValue("error_description", out var description) ? $": {description}" : ""));
                    }
                }

                await WriteResponseAsync(stream, result is not null, cts.Token);

                if (result is { } match)
                    return match.code;
                // Otherwise (e.g. a favicon request) keep listening until the callback arrives.
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CliException(
                $"Timed out after {timeout.TotalMinutes:0} minutes waiting for the OAuth redirect on {redirectUri}. " +
                "Complete the sign-in in the browser and try again.");
        }
        finally
        {
            listener.Stop();
        }
    }

    public static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No browser available (headless); the printed URL is the fallback.
        }
    }

    // ------------------------------------------------------------ internals

    private static async Task<string> ReadRequestHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();
        while (builder.Length < 65536)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
                break;
            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }
        return builder.ToString();
    }

    private static async Task WriteResponseAsync(NetworkStream stream, bool success, CancellationToken ct)
    {
        var message = success
            ? "<html><body><h3>Sign-in complete.</h3>You can close this window and return to the terminal.</body></html>"
            : "<html><body><h3>Still waiting for the sign-in to complete...</h3></body></html>";
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\n" +
            $"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static string? ExtractRequestTarget(string request)
    {
        var firstLine = request.Split("\r\n", 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (firstLine is null)
            return null;
        var parts = firstLine.Split(' ');
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equalsIndex = pair.IndexOf('=');
            if (equalsIndex < 0)
            {
                result[Uri.UnescapeDataString(pair)] = "";
                continue;
            }
            result[Uri.UnescapeDataString(pair[..equalsIndex])] = Uri.UnescapeDataString(pair[(equalsIndex + 1)..]);
        }
        return result;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
