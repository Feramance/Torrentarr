namespace Commandarr.Core.Configuration;

/// <summary>
/// Root configuration model matching qBitrr config.toml structure
/// </summary>
public class CommandarrConfig
{
    public SettingsConfig Settings { get; set; } = new();
    public QBitConfig QBit { get; set; } = new();
    public WebUIConfig WebUI { get; set; } = new();
    public Dictionary<string, ArrInstanceConfig> ArrInstances { get; set; } = new();

    /// <summary>
    /// Helper property to get Arr instances as a list
    /// </summary>
    public List<ArrInstanceConfig> Arrs => ArrInstances.Values.ToList();
}

public class SettingsConfig
{
    public string ConfigVersion { get; set; } = "5.8.8";
    public string ConsoleLevel { get; set; } = "INFO";
    public bool Logging { get; set; } = true;
    public string CompletedDownloadFolder { get; set; } = "";
    public string FreeSpace { get; set; } = "-1";
    public string FreeSpaceFolder { get; set; } = "";
    public bool AutoPauseResume { get; set; } = true;
    public int NoInternetSleepTimer { get; set; } = 15;
    public int LoopSleepTimer { get; set; } = 5;
    public int SearchLoopDelay { get; set; } = -1;
    public string FailedCategory { get; set; } = "failed";
    public string RecheckCategory { get; set; } = "recheck";
    public bool Tagless { get; set; } = false;
    public int IgnoreTorrentsYoungerThan { get; set; } = 180;
    public List<string> PingURLS { get; set; } = new() { "one.one.one.one", "dns.google.com" };
    public bool FFprobeAutoUpdate { get; set; } = true;
    public bool AutoUpdateEnabled { get; set; } = false;
    public string AutoUpdateCron { get; set; } = "0 3 * * 0";
    public bool AutoRestartProcesses { get; set; } = true;
    public int MaxProcessRestarts { get; set; } = 5;
    public int ProcessRestartWindow { get; set; } = 300;
    public int ProcessRestartDelay { get; set; } = 5;

    // Seeding configuration
    public List<CategorySeedingRule>? CategorySeedingRules { get; set; }
    public List<TrackerRule>? TrackerRules { get; set; }

    // Free space configuration
    public double? FreeSpaceThresholdGB { get; set; } = 10;
}

public class CategorySeedingRule
{
    public string Category { get; set; } = "";
    public int MinimumSeedingTime { get; set; } = 0; // minutes
    public double MinimumRatio { get; set; } = 0;
}

public class TrackerRule
{
    public string TrackerUrl { get; set; } = "";
    public int MinimumSeedingTime { get; set; } = 0; // minutes
    public double MinimumRatio { get; set; } = 0;
}

public class QBitConfig
{
    public bool Disabled { get; set; } = false;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 8080;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string? DownloadPath { get; set; }
    public List<string> ManagedCategories { get; set; } = new();
    public List<TrackerConfig> Trackers { get; set; } = new();
    public CategorySeedingConfig CategorySeeding { get; set; } = new();
}

public class CategorySeedingConfig
{
    public int DownloadRateLimitPerTorrent { get; set; } = -1;
    public int UploadRateLimitPerTorrent { get; set; } = -1;
    public double MaxUploadRatio { get; set; } = -1;
    public int MaxSeedingTime { get; set; } = -1;
    public int RemoveMode { get; set; } = 0; // 0=Never, 1=IfMetUploadLimit, 2=Always
    public bool HitAndRunMode { get; set; } = false;
    public double MinSeedRatio { get; set; } = 0;
    public int MinSeedTime { get; set; } = 0;
}

public class TrackerConfig
{
    public string Uri { get; set; } = "";
    public double? MaxUploadRatio { get; set; }
    public int? MaxSeedingTime { get; set; }
    public int? RemoveMode { get; set; }
    public bool? HitAndRunMode { get; set; }
    public double? MinSeedRatio { get; set; }
    public int? MinSeedTime { get; set; }
}

public class WebUIConfig
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 6969;
    public string Token { get; set; } = "";
    public bool LiveArr { get; set; } = true;
    public bool GroupSonarr { get; set; } = true;
    public bool GroupLidarr { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    public string ViewDensity { get; set; } = "Comfortable";
}

public class ArrInstanceConfig
{
    public string URI { get; set; } = "";
    public string APIKey { get; set; } = "";
    public bool Managed { get; set; } = true;
    public string Category { get; set; } = "";
    public string Type { get; set; } = ""; // radarr, sonarr, lidarr
    public bool SearchOnly { get; set; } = false;
    public bool ProcessingOnly { get; set; } = false;
    public List<TrackerConfig> Trackers { get; set; } = new();
    public CategorySeedingConfig? CategorySeeding { get; set; }
}
