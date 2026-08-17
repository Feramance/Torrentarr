namespace Torrentarr.Core.Configuration;

/// <summary>
/// Resolves catalog route slugs (qBit category or Arr section name) to the
/// <c>ArrInstance</c> values workers stamp in SQLite.
/// </summary>
public static class ArrCatalogIdentity
{
    /// <summary>
    /// Keys that may appear in <c>ArrInstance</c> columns for one catalog slug.
    /// Workers store the ArrInstances dictionary key (e.g. <c>Readarr-Books</c>);
    /// the WebUI passes <c>Category</c> (e.g. <c>readarr-books</c>). Older rows and
    /// tests may use either. qBitrr queries both (<c>_lidarr_instance_keys</c> parity).
    /// </summary>
    public static List<string> QueryKeys(
        IReadOnlyDictionary<string, ArrInstanceConfig>? instances,
        string? categoryOrName)
    {
        var keys = new List<string>();
        Add(keys, categoryOrName);

        if (instances is null)
            return keys.Count > 0 ? keys : [categoryOrName ?? ""];

        foreach (var kvp in instances)
        {
            if (string.Equals(kvp.Key, categoryOrName, StringComparison.OrdinalIgnoreCase)
                || CategoryPathHelper.CategoryEquals(kvp.Value.Category, categoryOrName))
            {
                Add(keys, kvp.Key);
                Add(keys, kvp.Value.Category);
            }
        }

        return keys.Count > 0 ? keys : [categoryOrName ?? ""];
    }

    public static List<string> QueryKeys(TorrentarrConfig config, string? categoryOrName)
        => QueryKeys(config.ArrInstances, categoryOrName);

    public static List<string> QueryKeys(KeyValuePair<string, ArrInstanceConfig> instance)
    {
        var keys = new List<string>();
        Add(keys, instance.Key);
        Add(keys, instance.Value.Category);
        return keys.Count > 0 ? keys : [instance.Key];
    }

    private static void Add(List<string> keys, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (keys.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
            return;
        keys.Add(value);
    }
}
