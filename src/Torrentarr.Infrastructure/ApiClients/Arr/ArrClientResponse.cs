using System.Net;
using Newtonsoft.Json;
using RestSharp;
using Torrentarr.Infrastructure.Http;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

internal static class ArrClientResponse
{
    internal static Task<RestResponse> ExecuteAsync(RestClient client, RestRequest request, CancellationToken ct) =>
        HttpRetryHelper.ExecuteArrAsync(client, request, ct);

    internal static void EnsureSuccess(RestResponse response, string operation)
    {
        if (response.ResponseStatus != ResponseStatus.Completed)
        {
            throw new ArrApiException(
                $"Arr API {operation} failed: {response.ResponseStatus}",
                null,
                response.ErrorMessage);
        }

        var statusCode = (int)response.StatusCode;
        if (statusCode is >= 200 and < 300)
            return;

        var status = statusCode != 0
            ? $"{statusCode} {response.StatusDescription}"
            : response.ResponseStatus.ToString();

        throw new ArrApiException(
            $"Arr API {operation} failed: HTTP {status}",
            statusCode != 0 ? (HttpStatusCode)statusCode : null,
            response.ErrorMessage);
    }

    internal static T DeserializeOrDefault<T>(string? content) where T : new()
    {
        if (string.IsNullOrWhiteSpace(content))
            return new T();

        return JsonConvert.DeserializeObject<T>(content) ?? new T();
    }

    /// <summary>
    /// pyarr-unmapped HTTP 4xx/5xx (including 415) that should skip one series, not kill the worker.
    /// </summary>
    internal static bool IsArrHttpError(Exception ex)
    {
        if (ex is ArrApiException { StatusCode: { } code })
        {
            var n = (int)code;
            return n is >= 400 and <= 599;
        }
        return false;
    }

    /// <summary>
    /// Connectivity / incomplete HTTP that should abort remaining series fetches.
    /// </summary>
    internal static bool IsArrTransportError(Exception ex)
        => ex is HttpRequestException or TimeoutException or TaskCanceledException
           || (ex is ArrApiException api && api.StatusCode is null);

    internal static void RaiseIfAllEpisodeFetchesFailed(int attempted, int failed, Exception? lastFailure)
    {
        if (attempted > 0 && failed == attempted && lastFailure != null)
            throw lastFailure;
    }
}
