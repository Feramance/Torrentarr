namespace Torrentarr.Infrastructure.ApiClients.QBittorrent;

internal readonly record struct QBittorrentLoginCookie(string Name, string Value);

/// <summary>
/// Parses qBittorrent WebUI login responses for 4.x/5.1 (<c>200 Ok.</c> + <c>SID</c>)
/// and 5.2+ (<c>204</c> empty body + <c>QBT_SID_{port}</c>).
/// </summary>
internal static class QBittorrentLoginParser
{
    public static bool TryAccept(
        int statusCode,
        string? content,
        IReadOnlyList<QBittorrentLoginCookie> cookies,
        out string? cookieHeader,
        out string failureReason)
    {
        cookieHeader = null;
        var body = content?.Trim() ?? "";

        if (body.Equals("Fails.", StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "qBittorrent rejected credentials (Fails.)";
            return false;
        }

        if (statusCode < 200 || statusCode > 299)
        {
            failureReason = $"HTTP {statusCode}";
            return false;
        }

        var selected = SelectSessionCookie(cookies);
        if (selected is null)
        {
            failureReason = cookies.Count == 0
                ? "no Set-Cookie on login response"
                : $"no SID/QBT_SID cookie (got: {string.Join(", ", cookies.Select(c => c.Name))})";
            return false;
        }

        cookieHeader = $"{selected.Value.Name}={selected.Value.Value}";
        failureReason = "";
        return true;
    }

    internal static QBittorrentLoginCookie? SelectSessionCookie(IReadOnlyList<QBittorrentLoginCookie> cookies)
    {
        foreach (var cookie in cookies)
        {
            if (string.IsNullOrEmpty(cookie.Name) || string.IsNullOrEmpty(cookie.Value))
                continue;
            if (cookie.Name.Equals("SID", StringComparison.OrdinalIgnoreCase)
                || cookie.Name.StartsWith("QBT_SID", StringComparison.OrdinalIgnoreCase)
                || cookie.Name.StartsWith("QBIT_SID", StringComparison.OrdinalIgnoreCase))
                return cookie;
        }

        return null;
    }
}
