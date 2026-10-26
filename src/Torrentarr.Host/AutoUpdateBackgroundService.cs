using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;

namespace Torrentarr.Host;

/// <summary>
/// §1.8: Cron-based auto-update background service.
/// When <c>Settings.AutoUpdateEnabled = true</c>, checks for new releases on the
/// <c>Settings.AutoUpdateCron</c> schedule and applies the update automatically.
/// </summary>
public class AutoUpdateBackgroundService : BackgroundService
{
    private readonly ILogger<AutoUpdateBackgroundService> _logger;
    private readonly TorrentarrConfig _config;
    private readonly UpdateService _updateService;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IConfigReloader? _reloader;

    public AutoUpdateBackgroundService(
        ILogger<AutoUpdateBackgroundService> logger,
        TorrentarrConfig config,
        UpdateService updateService,
        IHostApplicationLifetime lifetime, IConfigReloader? reloader = null)
    {
        _logger = logger;
        _config = config;
        _updateService = updateService;
        _lifetime = lifetime;
        _reloader = reloader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime? lastCheckMinute = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var configuration = _config.BeginOperation();
            var now = DateTime.UtcNow;
            var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            var cron = _config.Settings.AutoUpdateCron ?? "0 3 * * 0";
            if (_config.Settings.AutoUpdateEnabled && !UpdateService.IsSourceBuild() && lastCheckMinute != minute && CronSchedule.Matches(cron, now))
            {
                lastCheckMinute = minute;
                _logger.LogInformation("AutoUpdateBackgroundService: Cron matched at {Time}, checking for update", now);
                try
                {
                    await _updateService.CheckForUpdateAsync(forceRefresh: true, stoppingToken);
                    // Only apply if an update is actually available and no apply is already running
                    var meta = _updateService.BuildMetaResponse() as dynamic;
                    // Reflect into the anonymous type to read update_available
                    var props = _updateService.BuildMetaResponse().GetType().GetProperty("update_available");
                    var available = props?.GetValue(_updateService.BuildMetaResponse()) is bool b && b;
                    if (available && !_updateService.ApplyState.InProgress && _updateService.CanApplyBinaries)
                    {
                        _logger.LogInformation("AutoUpdateBackgroundService: Update available — applying");
                        await _updateService.ApplyUpdateAsync(_lifetime, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AutoUpdateBackgroundService: Check/apply failed");
                }
            }

            _reloader?.Acknowledge("auto-update", _config.Revision);
            if (_reloader != null) await _reloader.WaitForChangeAsync(_config.Revision, TimeSpan.FromSeconds(60 - DateTime.UtcNow.Second), stoppingToken);
            else await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="utcNow"/> matches the 5-field cron expression.
    /// Supports: literal values, '*' wildcard, comma-separated lists, and ranges (a-b).
    /// </summary>
    internal static bool MatchesCron(string cron, DateTime utcNow)
        => CronSchedule.Matches(cron, utcNow);
}
