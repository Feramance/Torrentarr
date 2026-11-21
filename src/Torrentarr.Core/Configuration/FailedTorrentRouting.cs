namespace Torrentarr.Core.Configuration;

public sealed class FailedTorrentDestination
{
    public string Client { get; set; } = "";
    public string Category { get; set; } = "";
}

public static class FailedTorrentRouting
{
    public const string Existing = "existing";
    public const string Move = "move";

    public static bool Enabled(ArrInstanceConfig arr) => arr.Torrent.FailedTorrentAction == Move;

    public static bool InScope(TorrentClientInstanceConfig client) =>
        !client.Disabled && client.Type.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(client.Host) && client.Host != "CHANGE_ME";

    public static string? DestinationError(TorrentarrConfig config, ArrInstanceConfig arr, string client, string category)
    {
        var normalized = CategoryPathHelper.NormalizeCategory(category);
        if (string.IsNullOrEmpty(normalized) || normalized != category || category.Any(char.IsControl))
            return "Select a valid existing category.";
        if (CategoryPathHelper.CategoryEquals(arr.Category, category)
            || CategoryPathHelper.CategoryEquals(config.Settings.FailedCategory, category)
            || CategoryPathHelper.CategoryEquals(config.Settings.RecheckCategory, category))
            return "The destination cannot be the source or a special processing category.";
        var owner = CategoryOwnershipHelper.ResolveOwningCategory(config, category, client);
        if (owner != null && (CategoryPathHelper.CategoryEquals(owner, config.Settings.FailedCategory)
            || CategoryPathHelper.CategoryEquals(owner, config.Settings.RecheckCategory)))
            return "The destination cannot belong to a special processing category.";
        if (owner != null && CategoryOwnershipHelper.FindArrSectionForCategory(config, owner) != null)
            return "The destination cannot be managed by an Arr integration.";
        return null;
    }

    public static void RenameReferences(TorrentarrConfig previous, TorrentarrConfig updated, Newtonsoft.Json.Linq.JObject? renames)
    {
        if (renames == null) return;
        foreach (var rename in renames.Properties())
        {
            if (previous.GetTorrentClient(rename.Name) == null || rename.Value is not Newtonsoft.Json.Linq.JValue { Value: string target })
                continue;
            foreach (var route in updated.ArrInstances.Values.SelectMany(arr => arr.Torrent.FailedTorrentDestinations))
                if (route.Client.Equals(rename.Name, StringComparison.OrdinalIgnoreCase))
                    route.Client = target;
        }
    }

    public static string? Validate(TorrentarrConfig config, ArrInstanceConfig arr)
    {
        if (arr.Torrent.FailedTorrentAction is not (Existing or Move))
            return "Select existing policy or move and keep files.";
        if (!Enabled(arr)) return null;
        var routes = arr.Torrent.FailedTorrentDestinations;
        var clients = config.GetAllTorrentClients();
        if (routes == null || routes.Any(route => route == null || string.IsNullOrWhiteSpace(route.Client)
                || !clients.ContainsKey(route.Client))
            || routes.Select(route => route.Client).Distinct(StringComparer.OrdinalIgnoreCase).Count() != routes.Count)
            return "Choose at most one destination for each configured torrent client.";
        foreach (var (client, settings) in clients)
        {
            if (!InScope(settings)) continue;
            var route = routes.SingleOrDefault(row => row.Client.Equals(client, StringComparison.OrdinalIgnoreCase));
            if (route == null) return $"Choose a failed torrent destination for '{client}'.";
            if (DestinationError(config, arr, client, route.Category) is { } error) return error;
        }
        return null;
    }
}
