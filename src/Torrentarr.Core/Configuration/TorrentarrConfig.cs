using System.Collections.Generic;
using Newtonsoft.Json;

namespace Torrentarr.Core.Configuration;

/// <summary>
/// Root configuration model matching qBitrr config.toml structure
/// </summary>
public class TorrentarrConfig
{
    internal HashSet<string>? MonitoredPolicyCategoriesCache { get; set; }
    internal object MonitoredPolicyCategoriesCacheLock { get; } = new();
    public SettingsConfig Settings { get; set; } = new();
    public Dictionary<string, QBitConfig> QBitInstances { get; set; } = new();
    public WebUIConfig WebUI { get; set; } = new();
    public Dictionary<string, ArrInstanceConfig> ArrInstances { get; set; } = new();
    public List<ArrInstanceConfig> Arrs => ArrInstances.Values.ToList();
}

public class SettingsConfig
{
    public string ConfigVersion { get; set; } = "6.14.3";
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
    /// <summary>Release channel: latest, stable, or nightly.</summary>
    public string AutoUpdateChannel { get; set; } = "latest";
    public bool AutoRestartProcesses { get; set; } = true;
    public int MaxProcessRestarts { get; set; } = 5;
    public int ProcessRestartWindow { get; set; } = 300;
    public int ProcessRestartDelay { get; set; } = 5;
    public List<CategorySeedingRule>? CategorySeedingRules { get; set; }
    public List<TrackerRule>? TrackerRules { get; set; }
    public double? FreeSpaceThresholdGB { get; set; } = 10;
    public string ImportMode { get; set; } = "Auto";
}

public class CategorySeedingRule
{
    public string Category { get; set; } = "";
    public int MinimumSeedingTime { get; set; }
    public double MinimumRatio { get; set; }
}

public class TrackerRule
{
    public string TrackerUrl { get; set; } = "";
    public int MinimumSeedingTime { get; set; }
    public double MinimumRatio { get; set; }
    public int Priority { get; set; }
}

public class QBitConfig
{
    public bool Disabled { get; set; }
    public string Host { get; set; } = "CHANGE_ME";
    public int Port { get; set; } = 8080;
    public string UserName { get; set; } = "CHANGE_ME";
    public string Password { get; set; } = "CHANGE_ME";
    /// <summary>When true, do not verify TLS certificates for this qBittorrent WebUI (self-signed certs).</summary>
    public bool SkipTLSVerify { get; set; }
    public string? DownloadPath { get; set; }
    public List<string> ManagedCategories { get; set; } = new();
    public bool MatchSubcategories { get; set; }
    public List<TrackerConfig> Trackers { get; set; } = new();
    public CategorySeedingConfig CategorySeeding { get; set; } = new();
}

public class CategorySeedingConfig
{
    public int DownloadRateLimitPerTorrent { get; set; } = -1;
    public int UploadRateLimitPerTorrent { get; set; } = -1;
    public double MaxUploadRatio { get; set; } = -1;
    public int MaxSeedingTime { get; set; } = -1;
    public int RemoveTorrent { get; set; } = -1;
    public string HitAndRunMode { get; set; } = "disabled";
    public double MinSeedRatio { get; set; } = 1.0;
    public int MinSeedingTimeDays { get; set; }
    public int HitAndRunMinimumDownloadPercent { get; set; } = 10;
    public double HitAndRunPartialSeedRatio { get; set; } = 1.0;
    public int TrackerUpdateBuffer { get; set; }
    public int StalledDelay { get; set; } = -1;
    public int IgnoreTorrentsYoungerThan { get; set; } = 180;
}

public class TrackerConfig
{
    public string? Name { get; set; }
    public string Uri { get; set; } = "";
    public int Priority { get; set; }
    public bool SortTorrents { get; set; }
    public double? MaxUploadRatio { get; set; }
    public int? MaxSeedingTime { get; set; }
    public int? RemoveTorrent { get; set; }
    public string? HitAndRunMode { get; set; }
    public double? MinSeedRatio { get; set; }
    public int? MinSeedingTimeDays { get; set; }
    public int? HitAndRunMinimumDownloadPercent { get; set; }
    public double? HitAndRunPartialSeedRatio { get; set; }
    public int? DownloadRateLimit { get; set; }
    public int? UploadRateLimit { get; set; }
    public int? MaxETA { get; set; }
    public int? TrackerUpdateBuffer { get; set; }
    public bool? SuperSeedMode { get; set; }
    public bool RemoveIfExists { get; set; }
    public bool AddTrackerIfMissing { get; set; }
    public List<string> AddTags { get; set; } = new();
}

public class WebUIConfig
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 6969;
    public string Token { get; set; } = "";
    public bool AuthDisabled { get; set; }
    public bool BehindHttpsProxy { get; set; }
    public string UrlBase { get; set; } = "";
    public bool LocalAuthEnabled { get; set; }
    /// <summary>
    /// When AuthDisabled and Host is 0.0.0.0/::, must be true to acknowledge public exposure.
    /// Null means the key was omitted (legacy warn-only).
    /// </summary>
    public bool? AllowInsecureExposure { get; set; }
    /// <summary>
    /// When false, GET <c>?token=</c> is ignored. Null (omitted) defaults to true for legacy configs.
    /// </summary>
    public bool? AllowInsecureTokenQuery { get; set; }
    public bool AllowsInsecureTokenQuery => AllowInsecureTokenQuery ?? true;
    public bool OIDCEnabled { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool LiveArr { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    public string ViewDensity { get; set; } = "Comfortable";
    public OIDCConfig? OIDC { get; set; }
}

public class OIDCConfig
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Scopes { get; set; } = "openid profile";
    public string CallbackPath { get; set; } = "/signin-oidc";
    public bool RequireHttpsMetadata { get; set; } = true;
}

public class ArrInstanceConfig
{
    public string URI { get; set; } = "";
    public string APIKey { get; set; } = "";
    /// <summary>When true, do not verify TLS for this Servarr API. Does not affect Overseerr/Ombi.</summary>
    public bool SkipTLSVerify { get; set; }
    public bool Managed { get; set; } = true;
    public string Category { get; set; } = "";
    public string Type { get; set; } = ""; // radarr, sonarr, lidarr, readarr
    public bool SearchOnly { get; set; } = false;
    public bool ProcessingOnly { get; set; } = false;
    /// <summary>Override qBit <c>MatchSubcategories</c> for this Arr instance; null inherits from qBit.</summary>
    public bool? MatchSubcategories { get; set; }
    public bool ReSearch { get; set; } = true;
    public string? ImportMode { get; set; }
    public int RssSyncTimer { get; set; } = 1;
    public int RefreshDownloadsTimer { get; set; } = 1;
    public List<string> ArrErrorCodesToBlocklist { get; set; } = new();
    public TorrentConfig Torrent { get; set; } = new();
    [JsonProperty("EntrySearch")]
    public SearchConfig Search { get; set; } = new();
    public CategorySeedingConfig? SeedingMode { get; set; }
}

public class TorrentConfig
{
    public bool CaseSensitiveMatches { get; set; }
    public List<string> FolderExclusionRegex { get; set; } = new() { @"\bextras?\b", @"\bfeaturettes?\b", @"\bsamples?\b", @"\bscreens?\b" };
    public List<string> FileNameExclusionRegex { get; set; } = new() { @"\bsample\b", @"brarbg.com\b", @"\btrailer\b" };
    public List<string> FileExtensionAllowlist { get; set; } = new() { ".mp4", ".mkv", ".sub", ".ass", ".srt", ".!qB", ".parts" };
    public bool AutoDelete { get; set; }
    public int IgnoreTorrentsYoungerThan { get; set; } = 180;
    public int MaximumETA { get; set; } = -1;
    public double MaximumDeletablePercentage { get; set; } = 0.99;
    public bool DoNotRemoveSlow { get; set; } = true;
    public int StalledDelay { get; set; } = 15;
    public bool ReSearchStalled { get; set; }
    public bool RemoveDeadTrackers { get; set; }
    public List<string> RemoveTrackerWithMessage { get; set; } = new();
    public List<TrackerConfig> Trackers { get; set; } = new();
    public SeedingModeConfig? SeedingMode { get; set; }
}

public class SeedingModeConfig
{
    public int DownloadRateLimitPerTorrent { get; set; } = -1;
    public int UploadRateLimitPerTorrent { get; set; } = -1;
    public double MaxUploadRatio { get; set; } = -1;
    public int MaxSeedingTime { get; set; } = -1;
    public int RemoveTorrent { get; set; } = -1;
    public bool RemoveDeadTrackers { get; set; }
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
    public bool AlsoSearchSpecials { get; set; }
    public bool Unmonitored { get; set; }
    public int SearchLimit { get; set; } = 5;
    public bool SearchByYear { get; set; } = true;
    public bool SearchInReverse { get; set; }
    public int SearchRequestsEvery { get; set; } = 300;
    public bool DoUpgradeSearch { get; set; }
    public bool QualityUnmetSearch { get; set; }
    public bool CustomFormatUnmetSearch { get; set; }
    public bool ForceMinimumCustomFormat { get; set; }
    public bool SearchAgainOnSearchCompletion { get; set; } = true;
    public bool UseTempForMissing { get; set; }
    public bool KeepTempProfile { get; set; }
    public Dictionary<string, string> QualityProfileMappings { get; set; } = new();
    public bool ForceResetTempProfiles { get; set; }
    public int TempProfileResetTimeoutMinutes { get; set; }
    public int ProfileSwitchRetryAttempts { get; set; } = 3;
    public List<string> MainQualityProfile { get; set; } = new();
    public List<string> TempQualityProfile { get; set; } = new();
    public string SearchBySeries { get; set; } = "smart";
    public bool PrioritizeTodaysReleases { get; set; } = true;
    public OmbiConfig? Ombi { get; set; }
    public OverseerrConfig? Overseerr { get; set; }
}

public class OmbiConfig
{
    public bool SearchOmbiRequests { get; set; }
    public string OmbiURI { get; set; } = "";
    public string OmbiAPIKey { get; set; } = "";
    public bool ApprovedOnly { get; set; } = true;
    /// <summary>When true, do not verify TLS for Ombi HTTPS calls only.</summary>
    public bool SkipTLSVerify { get; set; }
}

public class OverseerrConfig
{
    public bool SearchOverseerrRequests { get; set; }
    public string OverseerrURI { get; set; } = "";
    public string OverseerrAPIKey { get; set; } = "";
    public bool ApprovedOnly { get; set; } = true;
    public bool Is4K { get; set; } = false;
    /// <summary>When true, do not verify TLS for Overseerr HTTPS calls only.</summary>
    public bool SkipTLSVerify { get; set; }
}
