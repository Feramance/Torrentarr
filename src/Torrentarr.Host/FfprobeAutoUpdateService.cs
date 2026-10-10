using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;

namespace Torrentarr.Host;

internal sealed class FfprobeAutoUpdateService(
    IMediaValidationService mediaValidation,
    TorrentarrConfig config,
    ILogger<FfprobeAutoUpdateService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.Settings.FFprobeAutoUpdate)
            return;

        try { await mediaValidation.UpdateFFprobeAsync(stoppingToken); }
        catch (Exception ex) { logger.LogWarning(ex, "FFprobe auto-update failed"); }
    }
}
