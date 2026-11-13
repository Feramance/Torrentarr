using Newtonsoft.Json.Linq;
using Torrentarr.Core.Models;

namespace Torrentarr.Core.Configuration;

public class ScopedTrackerConfig : TrackerConfig
{
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public List<string>? ClientIds { get; set; }

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public List<string>? ArrInstances { get; set; }
}

public static class TrackerRules
{
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;

    public static List<ScopedTrackerConfig> ReadJson(JToken? value)
    {
        if (value is null) return [];
        if (value is not JArray rows)
            throw new ArgumentException("Trackers must be an array.");
        foreach (var row in rows)
        {
            if (row is not JObject rule)
                throw new ArgumentException("Each tracker rule must be an object.");
            foreach (var key in new[] { "ClientIds", "ArrInstances" })
                if (rule.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var scope) &&
                    (scope is not JArray ids || ids.Any(id => id.Type != JTokenType.String)))
                    throw new ArgumentException($"Tracker {key} must be an array of IDs.");
        }
        return rows.ToObject<List<ScopedTrackerConfig>>()!;
    }

    public static string? Validate(TorrentarrConfig config)
    {
        if (config.Trackers == null) return "Trackers must be an array.";
        var clients = config.GetAllTorrentClients().Keys.ToHashSet(IdComparer);
        var integrations = config.ArrInstances.Keys.ToHashSet(IdComparer);
        for (var i = 0; i < config.Trackers.Count; i++)
        {
            var rule = config.Trackers[i];
            if (rule == null) return $"Tracker rule {i + 1} must be an object.";
            var label = string.IsNullOrWhiteSpace(rule.Name) ? $"Tracker rule {i + 1}" : $"Tracker '{rule.Name}' (rule {i + 1})";
            var ids = rule.ClientIds ?? rule.ArrInstances;
            if (ids is not { Count: > 0 } || (rule.ClientIds != null && rule.ArrInstances != null))
                return $"{label} requires exactly one nonempty scope: ClientIds or ArrInstances.";
            if (ids.Distinct(IdComparer).Count() != ids.Count)
                return $"{label} has duplicate scope IDs.";
            var configured = rule.ClientIds != null ? clients : integrations;
            foreach (var id in ids)
                if (!configured.Contains(id))
                    return $"{label} references unknown instance '{id}'. Reassign or remove the reference in Trackers.";
            if (TrackerProtectionValidation.Validate(rule.Protection) is { } error)
                return $"{label}: {error}";
        }
        return null;
    }

    public static IEnumerable<ScopedTrackerConfig> ImportNested(TorrentarrConfig config)
    {
        foreach (var (id, client) in config.GetAllTorrentClients())
            foreach (var rule in client.Trackers)
            {
                var scoped = JObject.FromObject(rule).ToObject<ScopedTrackerConfig>()!;
                scoped.ClientIds = [id];
                yield return scoped;
            }
        foreach (var (id, arr) in config.ArrInstances)
            foreach (var rule in arr.Torrent.Trackers)
            {
                var scoped = JObject.FromObject(rule).ToObject<ScopedTrackerConfig>()!;
                scoped.ArrInstances = [id];
                yield return scoped;
            }
    }

    public static void NormalizeNested(TorrentarrConfig config)
    {
        var imported = ImportNested(config).ToList();
        if (imported.Count == 0) return;
        config.Trackers.AddRange(imported);
        foreach (var client in config.GetAllTorrentClients().Values) client.Trackers.Clear();
        foreach (var arr in config.ArrInstances.Values) arr.Torrent.Trackers.Clear();
    }

    public static void RenameReferences(TorrentarrConfig current, TorrentarrConfig updated, JObject? renames)
    {
        if (renames == null) return;
        foreach (var rename in renames.Properties())
        {
            var isClient = current.GetTorrentClient(rename.Name) != null;
            var destination = rename.Value.Value<string>()!;
            foreach (var rule in updated.Trackers)
            {
                var ids = isClient ? rule.ClientIds : rule.ArrInstances;
                if (ids == null) continue;
                for (var i = 0; i < ids.Count; i++)
                    if (IdComparer.Equals(ids[i], rename.Name))
                        ids[i] = destination;
            }
        }
    }

    public static IEnumerable<TrackerConfig> ForClient(TorrentarrConfig config, string id) =>
        (config.GetTorrentClient(id)?.Trackers ?? [])
        .Concat(config.Trackers.Where(rule => rule.ClientIds?.Contains(id, IdComparer) == true));

    public static IEnumerable<TrackerConfig> ForIntegration(TorrentarrConfig config, string id, ArrInstanceConfig integration) =>
        integration.Torrent.Trackers.Concat(config.Trackers.Where(rule => rule.ArrInstances?.Contains(id, IdComparer) == true));

    public static IEnumerable<TrackerConfig> ForTorrent(TorrentarrConfig config, TorrentInfo torrent) =>
        ForClient(config, torrent.ClientInstanceId).Concat(config.ArrInstances
            .Where(pair => CategoryOwnershipHelper.ArrOwnsTorrent(config, pair.Value, torrent))
            .SelectMany(pair => ForIntegration(config, pair.Key, pair.Value)));

    public static IEnumerable<TrackerConfig> All(TorrentarrConfig config) =>
        config.Trackers.Cast<TrackerConfig>()
            .Concat(config.GetAllTorrentClients().Values.SelectMany(client => client.Trackers))
            .Concat(config.ArrInstances.Values.SelectMany(arr => arr.Torrent.Trackers));
}
