using System.Globalization;

namespace Torrentarr.Core.Configuration;

public static class FreeSpaceThreshold
{
    public static long Parse(string? value) => TryParse(value, out var bytes)
        ? bytes : throw new FormatException("Invalid free space threshold.");

    public static bool TryParse(string? value, out long bytes)
    {
        bytes = -1;
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "-1") return true;
        var text = value.Trim().ToUpperInvariant();
        var power = "KMGT".IndexOf(text[^1]) + 1;
        if (power > 0) text = text[..^1];
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return false;
        var multiplier = 1L << (10 * power);
        if (number > long.MaxValue / multiplier) return false;
        bytes = number * multiplier;
        return true;
    }
}
