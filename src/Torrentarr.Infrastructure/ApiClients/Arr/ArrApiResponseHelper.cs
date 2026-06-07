using Newtonsoft.Json;
using RestSharp;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

/// <summary>
/// Shared helpers for Arr API responses. Sync paths must not treat HTTP failures as empty data.
/// </summary>
internal static class ArrApiResponseHelper
{
    public static List<T> DeserializeListOrThrow<T>(RestResponse response, string operation)
    {
        if (!response.IsSuccessful)
        {
            var statusText = response.StatusDescription ?? response.ErrorMessage ?? "Unknown error";
            throw new HttpRequestException(
                $"{operation} failed: HTTP {(int)response.StatusCode} {statusText.Trim()}");
        }

        if (string.IsNullOrEmpty(response.Content))
            return new List<T>();

        return JsonConvert.DeserializeObject<List<T>>(response.Content) ?? new List<T>();
    }

    public static T DeserializeOrThrow<T>(RestResponse response, string operation) where T : new()
    {
        if (!response.IsSuccessful)
        {
            var statusText = response.StatusDescription ?? response.ErrorMessage ?? "Unknown error";
            throw new HttpRequestException(
                $"{operation} failed: HTTP {(int)response.StatusCode} {statusText.Trim()}");
        }

        if (string.IsNullOrEmpty(response.Content))
            return new T();

        return JsonConvert.DeserializeObject<T>(response.Content) ?? new T();
    }
}
