using System.Security.Cryptography;
using System.Text;

namespace Torrentarr.Infrastructure.Services;

/// <summary>Shared auth helpers for WebUI and Host: constant-time token comparison and public path detection.</summary>
public static class WebUIAuthHelpers
{
    /// <summary>Constant-time token comparison using SHA-256 hashes to avoid leaking length.</summary>
    public static bool TokenEquals(string? a, string? b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a ?? "");
        var bBytes = Encoding.UTF8.GetBytes(b ?? "");
        var aHash = SHA256.HashData(aBytes);
        var bHash = SHA256.HashData(bBytes);
        return CryptographicOperations.FixedTimeEquals(aHash, bHash);
    }

    /// <summary>Returns true if the path and method are allowed without authentication (login page, assets, health, web/login, web/logout, set-password, OIDC).</summary>
    public static bool IsPublicPath(string path, string method)
    {
        if (string.IsNullOrEmpty(path)) return true;
        if (path.Equals("/health", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/login", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/favicon-16x16.png", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/favicon-32x32.png", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/favicon-48x48.png", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/logov2-clean.png", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/manifest.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/sw.js", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/web/meta", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/web/login", StringComparison.OrdinalIgnoreCase) && (method == "GET" || method == "POST")) return true;
        if (path.Equals("/web/logout", StringComparison.OrdinalIgnoreCase) && (method == "GET" || method == "POST")) return true;
        if (path.Equals("/web/auth/set-password", StringComparison.OrdinalIgnoreCase) && method == "POST") return true;
        // OIDC: only GET allowed (challenge redirect; callback with code in query)
        if (path.StartsWith("/signin-oidc", StringComparison.OrdinalIgnoreCase) && method == "GET") return true;
        if (path.StartsWith("/web/auth/oidc/challenge", StringComparison.OrdinalIgnoreCase) && method == "GET") return true;
        return false;
    }
}
