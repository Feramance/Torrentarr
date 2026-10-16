using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

public sealed record SonarrApiInfo(string ApiVersion, SystemInfo SystemInfo);

public static class SonarrApiDiscovery
{
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal SonarrApiInfo? Info;
        internal DateTimeOffset Expires;
    }

    private static readonly ConcurrentDictionary<string, Entry> Cache = new();

    public static Task<SonarrApiInfo> ResolveAsync(string baseUrl, string apiKey, bool skipTlsVerify,
        CancellationToken ct = default, HttpClient? client = null) =>
        ResolveAsync(baseUrl, apiKey, skipTlsVerify, TimeProvider.System, ct, client);

    internal static async Task<SonarrApiInfo> ResolveAsync(string baseUrl, string apiKey, bool skipTlsVerify,
        TimeProvider clock, CancellationToken ct = default, HttpClient? client = null)
    {
        baseUrl = new Uri(baseUrl).AbsoluteUri.TrimEnd('/');
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{baseUrl}\n{apiKey}\n{skipTlsVerify}")));
        var entry = Cache.GetOrAdd(key, _ => new Entry());
        await entry.Gate.WaitAsync(ct);
        try
        {
            if (entry.Info != null && entry.Expires > clock.GetUtcNow()) return entry.Info;
            using var ownedClient = client == null ? new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = skipTlsVerify
                    ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator : null
            })
            { Timeout = TimeSpan.FromSeconds(30) } : null;
            var http = client ?? ownedClient!;

            async Task<string?> ReadAsync(string path, bool allowNotFound = false)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{path}");
                request.Headers.Add("X-Api-Key", apiKey);
                using var response = await http.SendAsync(request, ct);
                if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
                if (!response.IsSuccessStatusCode)
                    throw new ArrApiException($"Sonarr discovery GET /{path} failed: HTTP {(int)response.StatusCode}", response.StatusCode);
                return await response.Content.ReadAsStringAsync(ct);
            }

            async Task<SystemInfo> StatusAsync(string version)
            {
                var status = JsonConvert.DeserializeObject<SystemInfo>((await ReadAsync($"api/{version}/system/status"))!);
                if (status == null || string.IsNullOrWhiteSpace(status.Version))
                    throw new JsonException("Sonarr system status did not contain a version");
                return status;
            }

            var discovery = await ReadAsync("api", allowNotFound: true);
            string apiVersion;
            SystemInfo status;
            if (!string.IsNullOrWhiteSpace(discovery))
            {
                var current = JObject.Parse(discovery)["current"];
                apiVersion = current?.Type == JTokenType.String ? current.Value<string>()! : "";
                if (apiVersion is not ("v3" or "v5"))
                    throw new JsonException($"Unsupported Sonarr API version: {apiVersion}");
                status = await StatusAsync(apiVersion);
            }
            else
            {
                status = await StatusAsync("v3");
                var major = status.Version!.Split('.')[0];
                apiVersion = major switch
                {
                    "3" or "4" => "v3",
                    "5" => "v5",
                    _ => throw new JsonException($"Unsupported Sonarr version: {status.Version}")
                };
                if (apiVersion == "v5") status = await StatusAsync(apiVersion);
            }
            entry.Info = new SonarrApiInfo(apiVersion, status);
            entry.Expires = clock.GetUtcNow().AddMinutes(5);
            return entry.Info;
        }
        finally { entry.Gate.Release(); }
    }
}
