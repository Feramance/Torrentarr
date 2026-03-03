namespace Torrentarr.Core.Configuration;

/// <summary>
/// Root configuration model matching qBitrr config.toml structure
/// </summary>
public class TorrentarrConfig
{
    public SettingsConfig Settings { get; set; } = new();
    /// <summary>
    /// All qBittorrent instances keyed by section name ("qBit", "qBit-seedbox", …).
    /// </summary>
    public Dictionary<string, QBitConfig> QBitInstances { get; set; } = new();
    public WebUIConfig WebUI { get; set; } = new();
    public Dictionary<string, ArrInstanceConfig> ArrInstances { get; set; } = new();

    /// <summary>
    /// Helper property to get Arr instances as a list
    /// </summary>
    public List<ArrInstanceConfig> Arrs => ArrInstances.Values.ToList();
}

public class SettingsConfig
{
    public string ConfigVersion { get; set; } = "5.9.2";
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

    // Import configuration
    public string ImportMode { get; set; } = "Auto"; // Auto, Move, Copy
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
    public int Priority { get; set; } = 0;
}

public class QBitConfig
{
    public bool Disabled { get; set; } = false;
    public string Host { get; set; } = "CHANGE_ME";
    public int Port { get; set; } = 8080;
    public string UserName { get; set; } = "CHANGE_ME";
    public string Password { get; set; } = "CHANGE_ME";
    /// <summary>Set to true for qBittorrent v5+ which uses a different API authentication scheme.</summary>
    public bool V5 { get; set; } = false;
    public string? DownloadPath { get; set; }
    public List<string> ManagedCategories { get; set; } = new();
    public List<TrackerConfig> Trackers { get; set; } = new();
    public CategorySeedingConfig CategorySeeding { get; set; } = new();
}

public class CategorySeedingConfig
{
    public int DownloadRateLimitPerTorrent { get; set; } = -1; // KB/s
    public int UploadRateLimitPerTorrent { get; set; } = -1; // KB/s
    public double MaxUploadRatio { get; set; } = -1;
    public int MaxSeedingTime { get; set; } = -1; // seconds
    public int RemoveTorrent { get; set; } = -1; // -1=Never, 1=Ratio, 2=Time, 3=OR, 4=AND
    public string HitAndRunMode { get; set; } = "disabled"; // "and", "or", "disabled"
    public double MinSeedRatio { get; set; } = 1.0;
    public int MinSeedingTimeDays { get; set; } = 0;
    public int HitAndRunMinimumDownloadPercent { get; set; } = 10;
    public double HitAndRunPartialSeedRatio { get; set; } = 1.0;
    public int TrackerUpdateBuffer { get; set; } = 0; // seconds buffer for tracker stats
    /// <summary>§10 qBitrr parity: StalledDelay in minutes for qBit-managed categories.</summary>
    public int StalledDelay { get; set; } = 15; // minutes
    /// <summary>§10 qBitrr parity: IgnoreTorrentsYoungerThan in seconds for qBit-managed categories.</summary>
    public int IgnoreTorrentsYoungerThan { get; set; } = 180; // seconds
}

public class TrackerConfig
{
    public string? Name { get; set; } // Human-readable tracker name
    public string Uri { get; set; } = "";
    public int Priority { get; set; } = 0;
    public double? MaxUploadRatio { get; set; }
    public int? MaxSeedingTime { get; set; }
    public int? RemoveTorrent { get; set; }
    public string? HitAndRunMode { get; set; } // "and", "or", "disabled"
    public double? MinSeedRatio { get; set; }
    public int? MinSeedingTimeDays { get; set; } // days — matches qBitrr MinSeedingTimeDays / MinSeedingTime TOML key
    public int? HitAndRunMinimumDownloadPercent { get; set; }
    public double? HitAndRunPartialSeedRatio { get; set; }
    public int? DownloadRateLimit { get; set; } // KB/s
    public int? UploadRateLimit { get; set; } // KB/s
    public int? MaxETA { get; set; } // seconds
    public int? TrackerUpdateBuffer { get; set; }
    /// <summary>§3.5: Enable super-seed mode for torrents whose active tracker matches this config.</summary>
    public bool? SuperSeedMode { get; set; }
    /// <summary>§3.1: Remove this tracker URL from any torrent that already has it.</summary>
    public bool RemoveIfExists { get; set; } = false;
    /// <summary>§3.1: Inject this tracker URL into any torrent whose category matches but lacks this tracker.</summary>
    public bool AddTrackerIfMissing { get; set; } = false;
    /// <summary>§3.1: Tags to apply to any torrent whose active tracker matches this config.</summary>
    public List<string> AddTags { get; set; } = new();
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

    // Search/processing options
    public bool ReSearch { get; set; } = true;
    public string ImportMode { get; set; } = "Auto";
    public int RssSyncTimer { get; set; } = 1; // minutes
    public int RefreshDownloadsTimer { get; set; } = 1; // minutes
    public List<string> ArrErrorCodesToBlocklist { get; set; } = new();

    // Torrent processing options (includes Torrent.Trackers and Torrent.SeedingMode)
    public TorrentConfig Torrent { get; set; } = new();

    // Search configuration
    public SearchConfig Search { get; set; } = new();

    // Instance-level seeding configuration (deprecated, use Torrent.SeedingMode)
    public CategorySeedingConfig? SeedingMode { get; set; }
}

public class TorrentConfig
{
    public bool CaseSensitiveMatches { get; set; } = false;
    public List<string> FolderExclusionRegex { get; set; } = new() { @"\bextras?\b", @"\bfeaturettes?\b", @"\bsamples?\b", @"\bscreens?\b" };
    public List<string> FileNameExclusionRegex { get; set; } = new() { @"\bsample\b", @"brarbg.com\b", @"\btrailer\b" };
    public List<string> FileExtensionAllowlist { get; set; } = new() { ".mp4", ".mkv", ".sub", ".ass", ".srt", ".!qB", ".parts" };
    public bool AutoDelete { get; set; } = false;
    public int IgnoreTorrentsYoungerThan { get; set; } = 180;
    public int MaximumETA { get; set; } = -1;
    public double MaximumDeletablePercentage { get; set; } = 0.99;
    public bool DoNotRemoveSlow { get; set; } = true;
    public int StalledDelay { get; set; } = 15; // minutes
    public bool ReSearchStalled { get; set; } = false;
    /// <summary>Some configs place these directly on Torrent rather than Torrent.SeedingMode (e.g. Lidarr).</summary>
    public bool RemoveDeadTrackers { get; set; } = false;
    public List<string> RemoveTrackerWithMessage { get; set; } = new();
    /// <summary>Per-tracker overrides parsed from [[Arr.Torrent.Trackers]] array-of-tables.</summary>
    public List<TrackerConfig> Trackers { get; set; } = new();

    // Seeding mode configuration for Arr instances
    public SeedingModeConfig? SeedingMode { get; set; }
}

/// <summary>
/// Seeding mode configuration for Arr instances (Torrent.SeedingMode section)
/// Note: HnR settings are now tracker-only (removed from SeedingMode in v5.9.1)
/// </summary>
public class SeedingModeConfig
{
    public int DownloadRateLimitPerTorrent { get; set; } = -1; // KB/s
    public int UploadRateLimitPerTorrent { get; set; } = -1; // KB/s
    public double MaxUploadRatio { get; set; } = -1;
    public int MaxSeedingTime { get; set; } = -1; // seconds
    public int RemoveTorrent { get; set; } = -1; // -1=Never, 1=Ratio, 2=Time, 3=OR, 4=AND
    public bool RemoveDeadTrackers { get; set; } = false;
    public List<string> RemoveTrackerWithMessage { get; set; } = new()
    {
        "skipping tracker announce (unreachable)",
        "No such host is known",
        "unsupported URL protocol",
        "info hash is not authorized with this tracker"
    };
}

public class SearchConfig
{
    public bool SearchMissing { get; set; } = true;
    public bool AlsoSearchSpecials { get; set; } = false; // Sonarr only
    public bool Unmonitored { get; set; } = false;
    public int SearchLimit { get; set; } = 5;
    public bool SearchByYear { get; set; } = true;
    public bool SearchInReverse { get; set; } = false;
    public int SearchRequestsEvery { get; set; } = 300; // seconds
    public bool DoUpgradeSearch { get; set; } = false;
    public bool QualityUnmetSearch { get; set; } = false;
    public bool CustomFormatUnmetSearch { get; set; } = false;
    public bool ForceMinimumCustomFormat { get; set; } = false;
    public bool SearchAgainOnSearchCompletion { get; set; } = true;
    public bool UseTempForMissing { get; set; } = false;
    public bool KeepTempProfile { get; set; } = false;
    public Dictionary<string, string> QualityProfileMappings { get; set; } = new();
    public bool ForceResetTempProfiles { get; set; } = false;
    public int TempProfileResetTimeoutMinutes { get; set; } = 0;
    public int ProfileSwitchRetryAttempts { get; set; } = 3;
    public List<string> MainQualityProfile { get; set; } = new();
    public List<string> TempQualityProfile { get; set; } = new();
    public string SearchBySeries { get; set; } = "smart"; // true, false, "smart" (Sonarr only)
    public bool PrioritizeTodaysReleases { get; set; } = true; // Sonarr only

    // Ombi configuration
    public OmbiConfig? Ombi { get; set; }

    // Overseerr configuration
    public OverseerrConfig? Overseerr { get; set; }
}

public class OmbiConfig
{
    public bool SearchOmbiRequests { get; set; } = false;
    public string OmbiURI { get; set; } = "";
    public string OmbiAPIKey { get; set; } = "";
    public bool ApprovedOnly { get; set; } = true;
}

public class OverseerrConfig
{
    public bool SearchOverseerrRequests { get; set; } = false;
    public string OverseerrURI { get; set; } = "";
    public string OverseerrAPIKey { get; set; } = "";
    public bool ApprovedOnly { get; set; } = true;
    public bool Is4K { get; set; } = false;
}
