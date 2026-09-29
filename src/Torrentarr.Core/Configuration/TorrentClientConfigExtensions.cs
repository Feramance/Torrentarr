namespace Torrentarr.Core.Configuration;

public static class TorrentClientConfigExtensions
{
    public static IReadOnlyDictionary<string, TorrentClientInstanceConfig> GetAllTorrentClients(this TorrentarrConfig config)
    {
        var result = new Dictionary<string, TorrentClientInstanceConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, legacy) in config.QBitInstances)
            result[id] = legacy;
        foreach (var (id, client) in config.TorrentClients)
            result[id] = client;
        return result;
    }

    public static TorrentClientInstanceConfig? GetTorrentClient(this TorrentarrConfig config, string instanceId)
        => config.GetAllTorrentClients().GetValueOrDefault(instanceId);
}
