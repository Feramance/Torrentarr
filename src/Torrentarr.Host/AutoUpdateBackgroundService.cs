using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;

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

    public AutoUpdateBackgroundService(
        ILogger<AutoUpdateBackgroundService> logger,
        TorrentarrConfig config,
        UpdateService updateService,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _config = config;
        _updateService = updateService;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Settings.AutoUpdateEnabled)
        {
            _logger.LogDebug("AutoUpdateBackgroundService: AutoUpdateEnabled=false, exiting");
            return;
        }

        var cron = _config.Settings.AutoUpdateCron ?? "0 3 * * 0";
        _logger.LogInformation("AutoUpdateBackgroundService: Starting with schedule '{Cron}'", cron);

        // Align to the next minute boundary so the cron check fires once per minute
        var delayToNextMinute = TimeSpan.FromSeconds(60 - DateTime.UtcNow.Second);
        await Task.Delay(delayToNextMinute, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            if (MatchesCron(cron, now))
            {
                _logger.LogInformation("AutoUpdateBackgroundService: Cron matched at {Time}, checking for update", now);
                try
                {
                    await _updateService.CheckForUpdateAsync(forceRefresh: true, stoppingToken);
                    // Only apply if an update is actually available and no apply is already running
                    var meta = _updateService.BuildMetaResponse() as dynamic;
                    // Reflect into the anonymous type to read update_available
                    var props = _updateService.BuildMetaResponse().GetType().GetProperty("update_available");
                    var available = props?.GetValue(_updateService.BuildMetaResponse()) is bool b && b;
                    if (available && !_updateService.ApplyState.InProgress)
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

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="utcNow"/> matches the 5-field cron expression.
    /// Supports: literal values, '*' wildcard, comma-separated lists, and ranges (a-b).
    /// </summary>
    internal static bool MatchesCron(string cron, DateTime utcNow)
    {
        var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;

        // Cron fields: minute  hour  day-of-month  month  day-of-week (0=Sunday)
        return MatchField(parts[0], utcNow.Minute)
            && MatchField(parts[1], utcNow.Hour)
            && MatchField(parts[2], utcNow.Day)
            && MatchField(parts[3], utcNow.Month)
            && MatchField(parts[4], (int)utcNow.DayOfWeek);
    }

    private static bool MatchField(string field, int value)
    {
        if (field == "*") return true;

        // Comma-separated list: "1,3,5"
        if (field.Contains(','))
            return field.Split(',').Any(p => MatchSingleField(p.Trim(), value));

        return MatchSingleField(field, value);
    }

    private static bool MatchSingleField(string field, int value)
    {
        // Range: "1-5"
        if (field.Contains('-'))
        {
            var bounds = field.Split('-');
            if (bounds.Length == 2 && int.TryParse(bounds[0], out var lo) && int.TryParse(bounds[1], out var hi))
                return value >= lo && value <= hi;
            return false;
        }
        return int.TryParse(field, out var n) && n == value;
    }
}
