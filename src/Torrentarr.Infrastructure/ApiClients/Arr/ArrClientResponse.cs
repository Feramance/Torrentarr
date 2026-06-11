using System.Net;
using RestSharp;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

internal static class ArrClientResponse
{
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
}
