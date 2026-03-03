using System.Text.RegularExpressions;

namespace Torrentarr.Core.Configuration;

/// <summary>
/// Parses human-friendly duration strings (e.g. "5m", "7d", "48h", "1w") into seconds or minutes.
/// Backwards compatible: plain integers are returned as-is.
/// Matches qBitrr's duration_config.py behaviour.
/// </summary>
public static class DurationParser
{
    private static readonly Regex DurationPattern = new(@"^\s*(-?\d+)\s*([sSmMhHdDwW]?)\s*$", RegexOptions.Compiled);

    // Suffix → multiplier (to seconds)
    private static readonly Dictionary<char, long> SuffixToSeconds = new()
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
    /// Parse a config value to seconds. Accepts int (as-is), or string with optional suffix.
    /// Suffixes: s=seconds, m=minutes, h=hours, d=days, w=weeks, M=months (30 days).
    /// Plain number or unsuffixed string is treated as seconds (backwards compatibility).
    /// </summary>
    public static int ParseToSeconds(object? value, int fallback = -1)
    {
        if (value == null) return fallback;
        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (value is double d && d == Math.Truncate(d)) return (int)d;

        var s = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return fallback;

        var match = DurationPattern.Match(s);
        if (!match.Success)
        {
            return int.TryParse(s, out var parsed) ? parsed : fallback;
        }

        var num = long.Parse(match.Groups[1].Value);
        var rawSuffix = match.Groups[2].Value;

        if (string.IsNullOrEmpty(rawSuffix))
            return (int)num; // No suffix → treat as seconds

        // Uppercase M = month, everything else normalize to lowercase
        var suffixKey = rawSuffix == "M" ? 'M' : char.ToLowerInvariant(rawSuffix[0]);
        var mult = SuffixToSeconds.GetValueOrDefault(suffixKey, 1);
        return (int)(num * mult);
    }

    /// <summary>
    /// Parse a config value to minutes. Same rules as ParseToSeconds but returns minutes.
    /// Plain number or unsuffixed string is treated as minutes (backwards compatibility for timer fields).
    /// </summary>
    public static int ParseToMinutes(object? value, int fallback = -1)
    {
        if (value == null) return fallback;
        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (value is double d && d == Math.Truncate(d)) return (int)d;

        var s = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return fallback;

        var match = DurationPattern.Match(s);
        if (!match.Success)
        {
            return int.TryParse(s, out var parsed) ? parsed : fallback;
        }

        var num = long.Parse(match.Groups[1].Value);
        var rawSuffix = match.Groups[2].Value;

        if (string.IsNullOrEmpty(rawSuffix))
            return (int)num; // No suffix → treat as minutes

        var suffixKey = rawSuffix == "M" ? 'M' : char.ToLowerInvariant(rawSuffix[0]);
        var mult = SuffixToMinutes.GetValueOrDefault(suffixKey, 1);
        var minutes = num * mult;
        if (minutes > 0 && minutes < 1) return 1; // Round up sub-minute values
        return (int)minutes;
    }
}
