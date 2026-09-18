namespace AcuCli.Core;

/// <summary>
/// Connection settings for a single Acumatica instance. Authentication is OAuth 2.0 only;
/// the access token is resolved by the caller (e.g. from the token store) and attached as
/// a Bearer header.
/// </summary>
public sealed record AcuClientOptions
{
    /// <summary>Site root URL, including the instance folder (e.g. <c>https://acumatica.contou.com</c>).</summary>
    public required string Url { get; init; }

    /// <summary>OAuth access token attached as a Bearer header on API requests.</summary>
    public string? AccessToken { get; init; }

    /// <summary>Contract-based endpoint name. Acumatica ships the <c>Default</c> endpoint.</summary>
    public string EndpointName { get; init; } = "Default";

    /// <summary>Endpoint version, e.g. <c>25.200.001</c>. Null means not configured yet.</summary>
    public string? EndpointVersion { get; init; }

    /// <summary>Ignore TLS certificate errors (self-signed dev certificates).</summary>
    public bool IgnoreCertificateErrors { get; init; }

    /// <summary>Request timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 300;
}
