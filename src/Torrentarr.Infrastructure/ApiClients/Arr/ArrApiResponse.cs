using System.Net;
using Newtonsoft.Json;
using RestSharp;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

/// <summary>
/// Shared helpers for Arr API responses. Sync paths must not treat HTTP failures as empty lists.
/// </summary>
public static class ArrApiResponse
{
    public static List<T> ParseListOrThrow<T>(RestResponse response, string operation)
    {
        if (!IsSuccessfulResponse(response))
        {
            var detail = string.IsNullOrWhiteSpace(response.ErrorMessage)
                ? response.StatusDescription
                : response.ErrorMessage;
            throw new InvalidOperationException(
                $"{operation} failed: HTTP {(int)response.StatusCode} {detail}".Trim());
        }

        return JsonConvert.DeserializeObject<List<T>>(response.Content ?? "[]") ?? new List<T>();
    }

    private static bool IsSuccessfulResponse(RestResponse response)
    {
        if (response.ResponseStatus != ResponseStatus.Completed)
        {
            return false;
        }

        var code = (int)response.StatusCode;
        return code >= (int)HttpStatusCode.OK && code < (int)HttpStatusCode.MultipleChoices;
    }
}
