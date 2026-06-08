namespace Torrentarr.Core.Configuration;

/// <summary>
/// qBittorrent hierarchical category path helpers (qBitrr category_paths.py parity).
/// </summary>
public static class CategoryPathHelper
{
    public const char Separator = '/';

    public static string NormalizeCategory(object? value)
    {
        if (value is null)
            return "";
        var s = value.ToString()?.Trim() ?? "";
        if (s.Length == 0)
            return "";
        var parts = s.Split(Separator)
            .Select(seg => seg.Trim())
            .Where(seg => seg.Length > 0);
        return string.Join(Separator, parts);
    }

    public static IReadOnlyList<string> SplitCategory(string value)
    {
        var norm = NormalizeCategory(value);
        return norm.Length == 0 ? Array.Empty<string>() : norm.Split(Separator);
    }

    public static IReadOnlyList<string> CategoryParents(string value)
    {
        var parts = SplitCategory(value);
        if (parts.Count < 2)
            return Array.Empty<string>();
        return Enumerable.Range(1, parts.Count - 1)
            .Select(i => string.Join(Separator, parts.Take(i)))
            .ToList();
    }

    public static bool IsSubcategoryOf(string child, string parent)
    {
        var c = NormalizeCategory(child);
        var p = NormalizeCategory(parent);
        if (c.Length == 0 || p.Length == 0 || string.Equals(c, p, StringComparison.Ordinal))
            return false;
        return c.StartsWith(p + Separator, StringComparison.Ordinal);
    }

    public static bool HasSubcategorySeparator(object? value)
        => (value?.ToString() ?? "").Contains(Separator);

    /// <summary>
    /// Returns the configured key that owns <paramref name="category"/> (or null).
    /// With <paramref name="prefix"/> false: exact match after normalization.
    /// With <paramref name="prefix"/> true: longest configured ancestor wins.
    /// </summary>
    public static string? MatchesConfigured(string category, IEnumerable<string> configured, bool prefix = false)
    {
        var target = NormalizeCategory(category);
        if (target.Length == 0)
            return null;

        var normalised = configured
            .Select(raw => NormalizeCategory(raw))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(n => (Norm: n, Original: n))
            .ToList();

        if (normalised.Count == 0)
            return null;

        foreach (var (norm, original) in normalised)
        {
            if (string.Equals(norm, target, StringComparison.Ordinal))
                return original;
        }

        if (!prefix)
            return null;

        (int Depth, string Original)? best = null;
        foreach (var (norm, original) in normalised)
        {
            if (!IsSubcategoryOf(target, norm))
                continue;
            var depth = norm.Count(c => c == Separator) + 1;
            if (best is null || depth > best.Value.Depth)
                best = (depth, original);
        }

        return best?.Original;
    }

    public static IReadOnlyList<(string Parent, string Child)> FindOverlapConflicts(IEnumerable<string> configured)
    {
        var items = configured
            .Select(NormalizeCategory)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var outList = new List<(string, string)>();
        foreach (var parent in items)
        {
            foreach (var child in items)
            {
                if (string.Equals(parent, child, StringComparison.Ordinal))
                    continue;
                if (IsSubcategoryOf(child, parent))
                    outList.Add((parent, child));
            }
        }

        return outList;
    }

    public static bool CategoryEquals(string? a, string? b)
        => string.Equals(NormalizeCategory(a), NormalizeCategory(b), StringComparison.Ordinal);
}
