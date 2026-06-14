using Newtonsoft.Json.Linq;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Guards config PATCH endpoints from clearing secrets that were redacted or omitted in the UI payload.
/// </summary>
public static class SensitiveConfigHelper
{
    public const string RedactedPlaceholder = "[redacted]";

    private const string SensitiveKeyPattern = @"(apikey|api_key|token|password|secret|passkey|credential)";

    public static bool IsSensitiveDottedKey(string dottedKey)
    {
        if (string.IsNullOrEmpty(dottedKey) || !dottedKey.Contains('.'))
            return false;

        var lastPart = dottedKey[(dottedKey.LastIndexOf('.') + 1)..];
        return System.Text.RegularExpressions.Regex.IsMatch(
            lastPart, SensitiveKeyPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Returns true when an incoming change must not overwrite a non-empty stored secret
    /// (redacted placeholder, null, or empty string).
    /// </summary>
    public static bool ShouldPreserveExistingSensitiveValue(string dottedKey, JToken? newValue, string? currentValue)
    {
        if (!IsSensitiveDottedKey(dottedKey) || string.IsNullOrEmpty(currentValue))
            return false;

        if (newValue == null || newValue.Type == JTokenType.Null)
            return true;

        if (newValue.Type != JTokenType.String)
            return false;

        var incoming = newValue.ToString();
        return incoming == RedactedPlaceholder || string.IsNullOrEmpty(incoming);
    }

    public static string? GetDottedStringValue(JObject root, string dottedKey)
    {
        if (!dottedKey.Contains('.'))
            return root[dottedKey]?.Type == JTokenType.String ? root[dottedKey]!.ToString() : null;

        JToken? current = root;
        foreach (var part in dottedKey.Split('.'))
        {
            if (current is not JObject obj)
                return null;

            var prop = obj.Properties()
                .FirstOrDefault(p => p.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (prop == null)
                return null;

            current = prop.Value;
        }

        return current?.Type == JTokenType.String ? current.ToString() : null;
    }
}
