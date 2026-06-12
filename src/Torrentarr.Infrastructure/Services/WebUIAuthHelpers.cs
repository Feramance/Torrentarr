using System.Security.Cryptography;
using System.Text;
using Torrentarr.Core.Configuration;

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

    /// <summary>
    /// Returns true when POST /web/auth/set-password is allowed (qBitrr 5.12.2 bootstrap parity).
    /// Requires setup token (env, or WebUI.Token for first-time bootstrap only) unless authenticated.
    /// </summary>
    public static bool IsSetPasswordAllowed(
        TorrentarrConfig cfg,
        string? setupToken,
        bool isAuthenticated,
        string? bearerOrQueryToken)
    {
        if (isAuthenticated)
            return true;

        // WebUI.Token as Bearer/query is for /api/* auth. Allow it for first-time bootstrap only;
        // password resets require setupToken (env) or an authenticated session.
        if (!string.IsNullOrWhiteSpace(bearerOrQueryToken)
            && !string.IsNullOrWhiteSpace(cfg.WebUI.Token)
            && TokenEquals(bearerOrQueryToken, cfg.WebUI.Token)
            && string.IsNullOrEmpty(cfg.WebUI.PasswordHash))
            return true;

        if (string.IsNullOrWhiteSpace(setupToken))
            return false;

        var envToken = Environment.GetEnvironmentVariable("TORRENTARR_SETUP_TOKEN")
            ?? Environment.GetEnvironmentVariable("QBITRR_SETUP_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken) && TokenEquals(setupToken, envToken))
            return true;

        // WebUI.Token in setupToken is bootstrap-only; password resets require TORRENTARR_SETUP_TOKEN.
        if (!string.IsNullOrWhiteSpace(cfg.WebUI.Token)
            && TokenEquals(setupToken, cfg.WebUI.Token)
            && string.IsNullOrEmpty(cfg.WebUI.PasswordHash))
            return true;

        return false;
    }
}
