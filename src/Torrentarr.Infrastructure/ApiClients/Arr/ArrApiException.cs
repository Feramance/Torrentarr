using System.Net;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

/// <summary>
/// Thrown when an Arr API request fails. Callers must not treat failures as empty data.
/// </summary>
public sealed class ArrApiException : Exception, Torrentarr.Core.Services.IDiagnosticException
{
    public ArrApiException(string message, HttpStatusCode? statusCode = null, string? errorMessage = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorMessage = errorMessage;
    }

    public string SafeDiagnostic => $"Arr API request failed; HTTP status={(StatusCode.HasValue ? ((int)StatusCode.Value).ToString() : "not supplied")}. Response body omitted to protect credentials.";

    public HttpStatusCode? StatusCode { get; }

    public string? ErrorMessage { get; }
}
