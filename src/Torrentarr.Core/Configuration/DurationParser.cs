using System.Globalization;
using System.Text.RegularExpressions;

namespace Torrentarr.Core.Configuration;

/// <summary>
/// Parses human-friendly duration strings (e.g. "5m", "7d", "48h", "1w", "1.5h") into seconds or minutes.
/// Backwards compatible: plain integers are returned as-is.
/// Matches qBitrr's duration_config.py behaviour, including TOML floats.
/// </summary>
public static class DurationParser
{
    private static readonly Regex DurationPattern = new(@"^\s*(-?\d+(?:\.\d+)?)\s*([sSmMhHdDwW]?)\s*$", RegexOptions.Compiled);

    // Suffix → multiplier (to seconds)
    private static readonly Dictionary<char, double> SuffixToSeconds = new()
    {
        ['s'] = 1,
        ['m'] = 60,
        ['h'] = 3600,
        ['d'] = 86400,
        ['w'] = 604800,
        ['M'] = 2592000, // 30 days
    };

    // Suffix → multiplier (to minutes)
    private static readonly Dictionary<char, double> SuffixToMinutes = new()
    {
        ['s'] = 1.0 / 60,
        ['m'] = 1,
        ['h'] = 60,
        ['d'] = 1440,
        ['w'] = 10080,
        ['M'] = 43200, // 30 days
    };

    /// <summary>
    /// Parse a config value to seconds. Accepts int (as-is), TOML float, or string with optional suffix.
    /// Suffixes: s=seconds, m=minutes, h=hours, d=days, w=weeks, M=months (30 days).
    /// Plain number or unsuffixed string is treated as seconds (backwards compatibility).
    /// </summary>
    public static int ParseToSeconds(object? value, int fallback = -1)
    {
        if (value == null) return fallback;
        if (TryGetDouble(value, out var numeric) && numeric.HasValue)
            return ToIntSeconds(numeric.Value);

        var s = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return fallback;

        var match = DurationPattern.Match(s);
        if (!match.Success)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? ToIntSeconds(parsed)
                : fallback;
        }

        var num = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var rawSuffix = match.Groups[2].Value;

        if (string.IsNullOrEmpty(rawSuffix))
            return ToIntSeconds(num);

        var suffixKey = rawSuffix == "M" ? 'M' : char.ToLowerInvariant(rawSuffix[0]);
        var mult = SuffixToSeconds.GetValueOrDefault(suffixKey, 1);
        return ToIntSeconds(num * mult);
    }

    /// <summary>
    /// Parse a config value to minutes. Same rules as ParseToSeconds but returns minutes.
    /// Plain number or unsuffixed string is treated as minutes (backwards compatibility for timer fields).
    /// </summary>
    public static int ParseToMinutes(object? value, int fallback = -1)
    {
        if (value == null) return fallback;
        if (TryGetDouble(value, out var numeric) && numeric.HasValue)
            return ToIntMinutes(numeric.Value);

        var s = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return fallback;

        var match = DurationPattern.Match(s);
        if (!match.Success)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? ToIntMinutes(parsed)
                : fallback;
        }

        var num = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var rawSuffix = match.Groups[2].Value;

        if (string.IsNullOrEmpty(rawSuffix))
            return ToIntMinutes(num);

        var suffixKey = rawSuffix == "M" ? 'M' : char.ToLowerInvariant(rawSuffix[0]);
        var mult = SuffixToMinutes.GetValueOrDefault(suffixKey, 1);
        return ToIntMinutes(num * mult);
    }

    private static bool TryGetDouble(object value, out double? result)
    {
        result = value switch
        {
            int i => i,
            long l => l,
            float f => f,
            double d => d,
            decimal m => (double)m,
            _ => null
        };
        return result.HasValue;
    }

    private static int ToIntSeconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return -1;
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static int ToIntMinutes(double minutes)
    {
        if (double.IsNaN(minutes) || double.IsInfinity(minutes))
            return -1;
        if (minutes > 0 && minutes < 1) return 1; // Round up sub-minute values
        return (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
    }
}
