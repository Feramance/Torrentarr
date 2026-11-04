using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Torrentarr.Core.Services;

/// <summary>Safe operational evidence: URLs never expose paths, queries or user information.</summary>
public interface IDiagnosticException
{
    string SafeDiagnostic { get; }
}

public static partial class DiagnosticText
{
    public static string Safe(string? value)
    {
        var text = Url().Replace(value ?? "", match => Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}:{uri.Port}/[redacted]" : "[redacted URL]");
        text = Secret().Replace(text, "$1=[redacted]");
        text = Bearer().Replace(text, "Bearer [redacted]");
        return ControlCharacters().Replace(text, " ");
    }

    public static string Error(Exception error) => error switch
    {
        IDiagnosticException diagnostic => Safe(diagnostic.SafeDiagnostic),
        AggregateException aggregate => string.Join("; ", aggregate.Flatten().InnerExceptions.Select(Error)),
        HttpRequestException http => $"{(http.Message.Contains("outcome is uncertain", StringComparison.OrdinalIgnoreCase) ? "Remote deletion outcome is uncertain; verify the torrent on the client before retrying. " : "")}HTTP request failed ({http.GetType().Name}; status={(http.StatusCode is { } status ? ((int)status).ToString() : "no response")}; transport={http.HttpRequestError}). {InnerSocket(http)}",
        SocketException socket => $"Network failure: {socket.SocketErrorCode}.",
        TaskCanceledException => "Request timed out or was cancelled before completion.",
        UnauthorizedAccessException => "Access denied by the operating system; check the service user's permissions.",
        System.Text.Json.JsonException json => $"Invalid JSON response at line {json.LineNumber}, byte {json.BytePositionInLine}.",
        _ => $"{error.GetType().Name}: {Safe(error.Message)}"
    };

    private static string InnerSocket(Exception error)
    {
        for (var current = error.InnerException; current != null; current = current.InnerException)
            if (current is SocketException socket) return $"Socket error={socket.SocketErrorCode}.";
        return "No verified lower-level network diagnosis is available.";
    }

    [GeneratedRegex(@"https?://[^\s\""'<>]+", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Url();
    [GeneratedRegex(@"\b(password|passwd|token|apikey|api_key|passkey|authorization|cookie|secret)[\""']?\s*[=:]\s*(?:\""[^\""\r\n]*\""|'[^'\r\n]*'|[^\s;,]+)(?:\s+[^\s;,]+)?", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Secret();
    [GeneratedRegex(@"\bBearer\s+[^\s;,]+", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Bearer();
    [GeneratedRegex(@"[\x00-\x1f\x7f-\x9f\u2028\u2029]", RegexOptions.NonBacktracking)]
    private static partial Regex ControlCharacters();
}
