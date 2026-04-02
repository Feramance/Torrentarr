namespace Torrentarr.Infrastructure.Services;

internal static class FreeSpaceParser
{
    public static long ParseFreeSpaceString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "-1") return -1;
        var v = value.Trim().ToUpperInvariant();
        try
        {
            if (v.EndsWith("G")) return long.Parse(v[..^1]) * 1024L * 1024L * 1024L;
            if (v.EndsWith("M")) return long.Parse(v[..^1]) * 1024L * 1024L;
            if (v.EndsWith("K")) return long.Parse(v[..^1]) * 1024L;
            return long.Parse(v);
        }
        catch { return -1; }
    }
}
