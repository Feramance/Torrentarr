using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;

namespace Torrentarr.Infrastructure.ApiClients.QBittorrent;

public sealed class QBittorrentTorrentClient : QBittorrentClient
{
    public QBittorrentTorrentClient(string instanceId, TorrentClientInstanceConfig config)
        : base(config.Host, config.Port, config.UserName, config.Password, config.SkipTLSVerify, instanceId)
    {
    }
}

public sealed class QBittorrentTorrentClientFactory : ITorrentClientFactory
{
    public string Type => "qbittorrent";

    public ITorrentClient Create(string instanceId, TorrentClientInstanceConfig config)
        => new QBittorrentTorrentClient(instanceId, config);
}
