using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;

namespace Torrentarr.Infrastructure.ApiClients.QBittorrent;

public sealed class QBittorrentTorrentClientFactory : ITorrentClientFactory
{
    public string Type => "qbittorrent";

    public ITorrentClient Create(string instanceId, TorrentClientInstanceConfig config)
        => new QBittorrentClient(config.Host, config.Port, config.UserName, config.Password, config.SkipTLSVerify, instanceId,
            config.AuthMode switch
            {
                "legacy" => null,
                "api-key" when ConfigValidationHelper.IsQBitApiKey(config.APIKey) => config.APIKey,
                "api-key" => throw new InvalidOperationException("Configure a valid qBittorrent API key."),
                _ => throw new InvalidOperationException("Unknown qBittorrent authentication mode.")
            });
}
