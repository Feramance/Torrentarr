using System.Net.Http;
using System.Net.Security;
using RestSharp;

namespace Torrentarr.Infrastructure.Http;

/// <summary>
/// qBitrr <c>SkipTLSVerify</c> parity: optional TLS certificate bypass for
/// qBittorrent, Servarr, Ombi, and Overseerr HTTPS clients.
/// </summary>
public static class TlsSkipHelper
{
    public static RestClientOptions CreateRestOptions(string baseUrl, bool skipTlsVerify, TimeSpan? timeout = null)
    {
        var options = new RestClientOptions(baseUrl.TrimEnd('/'))
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };
        if (skipTlsVerify)
            options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        return options;
    }

    /// <summary>
    /// Long-lived <see cref="HttpClient"/> for Ombi/Overseerr. Callers should cache
    /// instances (one verifying, one skipping) rather than creating per request.
    /// </summary>
    public static HttpClient CreateHttpClient(bool skipTlsVerify, TimeSpan? timeout = null)
    {
        HttpMessageHandler handler = skipTlsVerify
            ? new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            }
            : new HttpClientHandler();

        return new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        };
    }

    public static bool HasSkipCallback(RestClientOptions options) =>
        options.RemoteCertificateValidationCallback != null;
}
