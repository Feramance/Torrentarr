namespace Torrentarr.Core.Configuration;

/// <summary>Normalizes WebUI.UrlBase (qBitrr 5.12.3 parity).</summary>
public static class UrlBaseHelper
{
    public static string NormalizeUrlBase(object? value)
    {
        if (value is null)
            return "";
        var raw = value.ToString()?.Trim() ?? "";
        if (raw.Length == 0)
            return "";
        if (!raw.StartsWith('/'))
            raw = "/" + raw;
        return raw.TrimEnd('/');
    }
}
