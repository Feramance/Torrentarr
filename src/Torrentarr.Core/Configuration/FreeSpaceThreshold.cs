using System.Globalization;

namespace Torrentarr.Core.Configuration;

public static class FreeSpaceThreshold
{
    public static bool TryParse(string? value, out long bytes)
    {
        bytes = -1;
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "-1") return true;
        var text = value.Trim().ToUpperInvariant();
        var power = "KMGT".IndexOf(text[^1]) + 1;
        if (!long.TryParse(power > 0 ? text[..^1] : text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0)
            return false;
        try
        {
            checked { for (var i = 0; i < power; i++) number *= 1024; }
            bytes = number;
            return true;
        }
        catch (OverflowException) { return false; }
    }

    public static long Parse(string? value) => TryParse(value, out var bytes)
        ? bytes : throw new FormatException("FreeSpace must be -1 or a nonnegative integer with an optional K, M, G, or T suffix.");
}
