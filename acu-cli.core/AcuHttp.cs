namespace AcuCli.Core;

/// <summary>Shared HttpClient factory for the library.</summary>
internal static class AcuHttp
{
    public static HttpClient Create(bool ignoreCertificateErrors, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler();
        if (ignoreCertificateErrors)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

        var client = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(100),
        };
        return client;
    }
}
