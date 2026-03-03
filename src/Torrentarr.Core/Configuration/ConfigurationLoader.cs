using Tomlyn;
using Tomlyn.Model;

namespace Torrentarr.Core.Configuration;

/// <summary>
/// Loads configuration from TOML files with backwards compatibility for qBitrr config.toml
/// </summary>
public class ConfigurationLoader
{
    /// <summary>Expected config schema version (qBitrr parity). Used for validation and mismatch warning.</summary>
    public const string ExpectedConfigVersion = "5.9.2";

    private readonly string _configPath;

    public ConfigurationLoader(string? configPath = null)
    {
        _configPath = configPath ?? GetDefaultConfigPath();
    }

    public static string GetDefaultConfigPath()
    {
        // Allow test harnesses and Docker to override the config path via an environment variable
        var envOverride = Environment.GetEnvironmentVariable("TORRENTARR_CONFIG");
        if (!string.IsNullOrEmpty(envOverride))
            return envOverride;

        // Try multiple locations (same as qBitrr)
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var possiblePaths = new[]
        {
            Path.Combine(homePath, "config", "config.toml"),
            Path.Combine(homePath, ".config", "qbitrr", "config.toml"),
            Path.Combine(homePath, ".config", "torrentarr", "config.toml"),
            Path.Combine(Directory.GetCurrentDirectory(), "config.toml")
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
                return path;
        }

        // Default to first location
        return possiblePaths[0];
    }

    public TorrentarrConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            throw new FileNotFoundException($"Configuration file not found at: {_configPath}");
        }

        var tomlContent = File.ReadAllText(_configPath);
        var tomlTable = Toml.ToModel(tomlContent);

        var config = new TorrentarrConfig();

        // Parse Settings section
        if (tomlTable.TryGetValue("Settings", out var settingsObj) && settingsObj is TomlTable settingsTable)
        {
            config.Settings = ParseSettings(settingsTable);
        }

        // Parse [qBit] and [qBit-XXX] sections into the unified QBitInstances dictionary
        if (tomlTable.TryGetValue("qBit", out var qbitObj) && qbitObj is TomlTable qbitTable)
            config.QBitInstances["qBit"] = ParseQBit(qbitTable);

        foreach (var kvp in tomlTable)
        {
            if (kvp.Key.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase)
                && kvp.Value is TomlTable additionalQbitTable)
            {
                config.QBitInstances[kvp.Key] = ParseQBit(additionalQbitTable);
            }
        }

        // Parse WebUI section
        if (tomlTable.TryGetValue("WebUI", out var webuiObj) && webuiObj is TomlTable webuiTable)
        {
            config.WebUI = ParseWebUI(webuiTable);
        }

        // Parse Arr instances (Radarr-*, Sonarr-*, Lidarr-*)
        config.ArrInstances = ParseArrInstances(tomlTable);

        return config;
    }

    private SettingsConfig ParseSettings(TomlTable table)
    {
        var settings = new SettingsConfig();

        if (table.TryGetValue("ConfigVersion", out var configVersion))
            settings.ConfigVersion = configVersion?.ToString() ?? "5.9.0";

        if (table.TryGetValue("ConsoleLevel", out var consoleLevel))
            settings.ConsoleLevel = consoleLevel?.ToString() ?? "INFO";

        if (table.TryGetValue("Logging", out var logging))
            settings.Logging = Convert.ToBoolean(logging);

        if (table.TryGetValue("CompletedDownloadFolder", out var downloadFolder))
            settings.CompletedDownloadFolder = downloadFolder?.ToString() ?? "";

        if (table.TryGetValue("FreeSpace", out var freeSpace))
            settings.FreeSpace = freeSpace?.ToString() ?? "-1";

        if (table.TryGetValue("FreeSpaceFolder", out var freeSpaceFolder))
            settings.FreeSpaceFolder = freeSpaceFolder?.ToString() ?? "";

        if (table.TryGetValue("AutoPauseResume", out var autoPauseResume))
            settings.AutoPauseResume = Convert.ToBoolean(autoPauseResume);

        if (table.TryGetValue("NoInternetSleepTimer", out var noInternetSleep))
            settings.NoInternetSleepTimer = DurationParser.ParseToSeconds(noInternetSleep, 15);

        if (table.TryGetValue("LoopSleepTimer", out var loopSleep))
            settings.LoopSleepTimer = DurationParser.ParseToSeconds(loopSleep, 5);

        if (table.TryGetValue("SearchLoopDelay", out var searchDelay))
            settings.SearchLoopDelay = DurationParser.ParseToSeconds(searchDelay, -1);

        if (table.TryGetValue("FailedCategory", out var failedCat))
            settings.FailedCategory = failedCat?.ToString() ?? "failed";

        if (table.TryGetValue("RecheckCategory", out var recheckCat))
            settings.RecheckCategory = recheckCat?.ToString() ?? "recheck";

        if (table.TryGetValue("Tagless", out var tagless))
            settings.Tagless = Convert.ToBoolean(tagless);

        if (table.TryGetValue("IgnoreTorrentsYoungerThan", out var ignoreYounger))
            settings.IgnoreTorrentsYoungerThan = DurationParser.ParseToSeconds(ignoreYounger, 180);

        if (table.TryGetValue("PingURLS", out var pingUrls) && pingUrls is TomlArray pingArray)
            settings.PingURLS = pingArray.Select(x => x?.ToString() ?? "").ToList();

        if (table.TryGetValue("FFprobeAutoUpdate", out var ffprobeAuto))
            settings.FFprobeAutoUpdate = Convert.ToBoolean(ffprobeAuto);

        if (table.TryGetValue("AutoUpdateEnabled", out var autoUpdate))
            settings.AutoUpdateEnabled = Convert.ToBoolean(autoUpdate);

        if (table.TryGetValue("AutoUpdateCron", out var updateCron))
            settings.AutoUpdateCron = updateCron?.ToString() ?? "0 3 * * 0";

        if (table.TryGetValue("AutoRestartProcesses", out var autoRestart))
            settings.AutoRestartProcesses = Convert.ToBoolean(autoRestart);

        if (table.TryGetValue("MaxProcessRestarts", out var maxRestarts))
            settings.MaxProcessRestarts = Convert.ToInt32(maxRestarts);

        if (table.TryGetValue("ProcessRestartWindow", out var restartWindow))
            settings.ProcessRestartWindow = DurationParser.ParseToSeconds(restartWindow, 300);

        if (table.TryGetValue("ProcessRestartDelay", out var restartDelay))
            settings.ProcessRestartDelay = DurationParser.ParseToSeconds(restartDelay, 5);

        return settings;
    }

    private QBitConfig ParseQBit(TomlTable table)
    {
        var qbit = new QBitConfig();

        if (table.TryGetValue("Disabled", out var disabled))
            qbit.Disabled = Convert.ToBoolean(disabled);

        if (table.TryGetValue("Host", out var host))
            qbit.Host = host?.ToString() ?? "localhost";

        if (table.TryGetValue("Port", out var port))
            qbit.Port = Convert.ToInt32(port);

        if (table.TryGetValue("UserName", out var username))
            qbit.UserName = username?.ToString() ?? "";

        if (table.TryGetValue("Password", out var password))
            qbit.Password = password?.ToString() ?? "";

        if (table.TryGetValue("v5", out var v5))
            qbit.V5 = Convert.ToBoolean(v5);

        if (table.TryGetValue("DownloadPath", out var downloadPath))
            qbit.DownloadPath = downloadPath?.ToString();

        if (table.TryGetValue("ManagedCategories", out var categories) && categories is TomlArray catArray)
            qbit.ManagedCategories = catArray.Select(x => x?.ToString() ?? "").ToList();

        if (table.TryGetValue("CategorySeeding", out var seedingObj) && seedingObj is TomlTable seedingTable)
            qbit.CategorySeeding = ParseCategorySeeding(seedingTable);

        if (table.TryGetValue("Trackers", out var trackersObj) && trackersObj is TomlArray trackersArray)
            qbit.Trackers = trackersArray.Select(t => ParseTrackerConfig(t as TomlTable)).Where(t => t != null).ToList()!;

        return qbit;
    }

    private TrackerConfig? ParseTrackerConfig(TomlTable? table)
    {
        if (table == null) return null;

        var tracker = new TrackerConfig();

        if (table.TryGetValue("Name", out var name))
            tracker.Name = name?.ToString();

        if (table.TryGetValue("URI", out var uri))
            tracker.Uri = uri?.ToString() ?? "";

        if (table.TryGetValue("Priority", out var priority))
            tracker.Priority = Convert.ToInt32(priority);

        if (table.TryGetValue("MaxUploadRatio", out var maxRatio))
            tracker.MaxUploadRatio = Convert.ToDouble(maxRatio);

        if (table.TryGetValue("MaxSeedingTime", out var maxTime))
            tracker.MaxSeedingTime = DurationParser.ParseToSeconds(maxTime);

        if (table.TryGetValue("RemoveTorrent", out var removeTorrent))
            tracker.RemoveTorrent = Convert.ToInt32(removeTorrent);

        if (table.TryGetValue("HitAndRunMode", out var hnrMode))
            tracker.HitAndRunMode = ParseHitAndRunMode(hnrMode);

        if (table.TryGetValue("MinSeedRatio", out var minRatio))
            tracker.MinSeedRatio = Convert.ToDouble(minRatio);

        if (table.TryGetValue("MinSeedingTime", out var minTime))
            tracker.MinSeedingTimeDays = Convert.ToInt32(minTime);

        if (table.TryGetValue("HitAndRunMinimumDownloadPercent", out var hnrMinDlPct))
            tracker.HitAndRunMinimumDownloadPercent = Convert.ToInt32(hnrMinDlPct);

        if (table.TryGetValue("HitAndRunPartialSeedRatio", out var hnrPartialRatio))
            tracker.HitAndRunPartialSeedRatio = Convert.ToDouble(hnrPartialRatio);

        if (table.TryGetValue("DownloadRateLimit", out var downloadLimit))
            tracker.DownloadRateLimit = Convert.ToInt32(downloadLimit);

        if (table.TryGetValue("UploadRateLimit", out var uploadLimit))
            tracker.UploadRateLimit = Convert.ToInt32(uploadLimit);

        if (table.TryGetValue("MaxETA", out var maxEta))
            tracker.MaxETA = DurationParser.ParseToSeconds(maxEta);

        // qBitrr Arr tracker sections use MaximumETA (not MaxETA)
        if (table.TryGetValue("MaximumETA", out var maximumEta))
            tracker.MaxETA = DurationParser.ParseToSeconds(maximumEta);

        if (table.TryGetValue("TrackerUpdateBuffer", out var trackerUpdateBuffer))
            tracker.TrackerUpdateBuffer = DurationParser.ParseToSeconds(trackerUpdateBuffer, 0);

        if (table.TryGetValue("SuperSeedMode", out var superSeedMode))
            tracker.SuperSeedMode = Convert.ToBoolean(superSeedMode);

        // §3.1: Tracker management fields
        if (table.TryGetValue("RemoveIfExists", out var removeIfExists))
            tracker.RemoveIfExists = Convert.ToBoolean(removeIfExists);
        if (table.TryGetValue("AddTrackerIfMissing", out var addTrackerIfMissing))
            tracker.AddTrackerIfMissing = Convert.ToBoolean(addTrackerIfMissing);
        if (table.TryGetValue("AddTags", out var addTagsVal) && addTagsVal is Tomlyn.Model.TomlArray addTagsArr)
            tracker.AddTags = addTagsArr.OfType<string>().ToList();

        return tracker;
    }

    private CategorySeedingConfig ParseCategorySeeding(TomlTable table)
    {
        var seeding = new CategorySeedingConfig();

        if (table.TryGetValue("DownloadRateLimitPerTorrent", out var downloadLimit))
            seeding.DownloadRateLimitPerTorrent = Convert.ToInt32(downloadLimit);

        if (table.TryGetValue("UploadRateLimitPerTorrent", out var uploadLimit))
            seeding.UploadRateLimitPerTorrent = Convert.ToInt32(uploadLimit);

        if (table.TryGetValue("MaxUploadRatio", out var maxRatio))
            seeding.MaxUploadRatio = Convert.ToDouble(maxRatio);

        if (table.TryGetValue("MaxSeedingTime", out var maxTime))
            seeding.MaxSeedingTime = DurationParser.ParseToSeconds(maxTime);

        if (table.TryGetValue("RemoveMode", out var removeMode))
            seeding.RemoveTorrent = Convert.ToInt32(removeMode);

        if (table.TryGetValue("RemoveTorrent", out var removeTorrent))
            seeding.RemoveTorrent = Convert.ToInt32(removeTorrent);

        if (table.TryGetValue("HitAndRunMode", out var hnrMode))
            seeding.HitAndRunMode = ParseHitAndRunMode(hnrMode) ?? "disabled";

        if (table.TryGetValue("MinSeedRatio", out var minRatio))
            seeding.MinSeedRatio = Convert.ToDouble(minRatio);

        if (table.TryGetValue("MinSeedTime", out var minTime))
            seeding.MinSeedingTimeDays = Convert.ToInt32(minTime);

        if (table.TryGetValue("MinSeedingTimeDays", out var minSeedingTimeDays))
            seeding.MinSeedingTimeDays = Convert.ToInt32(minSeedingTimeDays);

        if (table.TryGetValue("HitAndRunMinimumDownloadPercent", out var hnrMinDlPct))
            seeding.HitAndRunMinimumDownloadPercent = Convert.ToInt32(hnrMinDlPct);

        if (table.TryGetValue("HitAndRunPartialSeedRatio", out var hnrPartialRatio))
            seeding.HitAndRunPartialSeedRatio = Convert.ToDouble(hnrPartialRatio);

        if (table.TryGetValue("TrackerUpdateBuffer", out var trackerUpdateBuffer))
            seeding.TrackerUpdateBuffer = DurationParser.ParseToSeconds(trackerUpdateBuffer, 0);

        if (table.TryGetValue("StalledDelay", out var stalledDelay))
            seeding.StalledDelay = DurationParser.ParseToMinutes(stalledDelay, 15);

        if (table.TryGetValue("IgnoreTorrentsYoungerThan", out var ignoreYounger))
            seeding.IgnoreTorrentsYoungerThan = DurationParser.ParseToSeconds(ignoreYounger, 180);

        return seeding;
    }

    private WebUIConfig ParseWebUI(TomlTable table)
    {
        var webui = new WebUIConfig();

        if (table.TryGetValue("Host", out var host))
            webui.Host = host?.ToString() ?? "0.0.0.0";

        if (table.TryGetValue("Port", out var port))
            webui.Port = Convert.ToInt32(port);

        if (table.TryGetValue("Token", out var token))
            webui.Token = token?.ToString() ?? "";

        if (table.TryGetValue("LiveArr", out var liveArr))
            webui.LiveArr = Convert.ToBoolean(liveArr);

        if (table.TryGetValue("GroupSonarr", out var groupSonarr))
            webui.GroupSonarr = Convert.ToBoolean(groupSonarr);

        if (table.TryGetValue("GroupLidarr", out var groupLidarr))
            webui.GroupLidarr = Convert.ToBoolean(groupLidarr);

        if (table.TryGetValue("Theme", out var theme))
            webui.Theme = theme?.ToString() ?? "Dark";

        if (table.TryGetValue("ViewDensity", out var viewDensity))
            webui.ViewDensity = viewDensity?.ToString() ?? "Comfortable";

        return webui;
    }

    private Dictionary<string, ArrInstanceConfig> ParseArrInstances(TomlTable rootTable)
    {
        var instances = new Dictionary<string, ArrInstanceConfig>();

        foreach (var kvp in rootTable)
        {
            var sectionName = kvp.Key;
            if (kvp.Value is not TomlTable instanceTable)
                continue;

            var lower = sectionName.ToLowerInvariant();
            bool isRadarr = lower == "radarr" || lower.StartsWith("radarr-");
            bool isSonarr = lower == "sonarr" || lower.StartsWith("sonarr-");
            bool isLidarr = lower == "lidarr" || lower.StartsWith("lidarr-");

            if (isRadarr || isSonarr || isLidarr)
            {
                var instance = new ArrInstanceConfig();

                if (isRadarr)
                    instance.Type = "radarr";
                else if (isSonarr)
                    instance.Type = "sonarr";
                else if (isLidarr)
                    instance.Type = "lidarr";

                if (instanceTable.TryGetValue("URI", out var uri))
                    instance.URI = uri?.ToString() ?? "";

                if (instanceTable.TryGetValue("APIKey", out var apiKey))
                    instance.APIKey = apiKey?.ToString() ?? "";

                if (instanceTable.TryGetValue("Managed", out var managed))
                    instance.Managed = Convert.ToBoolean(managed);

                if (instanceTable.TryGetValue("Category", out var category))
                    instance.Category = category?.ToString() ?? "";

                if (instanceTable.TryGetValue("SearchOnly", out var searchOnly))
                    instance.SearchOnly = Convert.ToBoolean(searchOnly);

                if (instanceTable.TryGetValue("ProcessingOnly", out var procOnly))
                    instance.ProcessingOnly = Convert.ToBoolean(procOnly);

                if (instanceTable.TryGetValue("ReSearch", out var reSearch))
                    instance.ReSearch = Convert.ToBoolean(reSearch);

                if (instanceTable.TryGetValue("importMode", out var importMode))
                    instance.ImportMode = importMode?.ToString() ?? "Auto";

                if (instanceTable.TryGetValue("RssSyncTimer", out var rssSyncTimer))
                    instance.RssSyncTimer = DurationParser.ParseToMinutes(rssSyncTimer, 1);

                if (instanceTable.TryGetValue("RefreshDownloadsTimer", out var refreshDownloadsTimer))
                    instance.RefreshDownloadsTimer = DurationParser.ParseToMinutes(refreshDownloadsTimer, 1);

                if (instanceTable.TryGetValue("ArrErrorCodesToBlocklist", out var arrErrorCodes) && arrErrorCodes is TomlArray errorArray)
                    instance.ArrErrorCodesToBlocklist = errorArray.Select(x => x?.ToString() ?? "").ToList();

                // Parse Torrent section
                if (instanceTable.TryGetValue("Torrent", out var torrentObj) && torrentObj is TomlTable torrentTable)
                {
                    instance.Torrent = ParseTorrentConfig(torrentTable);
                }

                // Parse EntrySearch section
                if (instanceTable.TryGetValue("EntrySearch", out var searchObj) && searchObj is TomlTable searchTable)
                {
                    instance.Search = ParseSearchConfig(searchTable, instance.Type);
                }

                instances[sectionName] = instance;
            }
        }

        return instances;
    }

    private TorrentConfig ParseTorrentConfig(TomlTable table)
    {
        var torrent = new TorrentConfig();

        if (table.TryGetValue("CaseSensitiveMatches", out var caseSensitive))
            torrent.CaseSensitiveMatches = Convert.ToBoolean(caseSensitive);

        if (table.TryGetValue("FolderExclusionRegex", out var folderExclusions) && folderExclusions is TomlArray folderArray)
            torrent.FolderExclusionRegex = folderArray.Select(x => x?.ToString() ?? "").ToList();

        if (table.TryGetValue("FileNameExclusionRegex", out var fileNameExclusions) && fileNameExclusions is TomlArray fileNameArray)
            torrent.FileNameExclusionRegex = fileNameArray.Select(x => x?.ToString() ?? "").ToList();

        if (table.TryGetValue("FileExtensionAllowlist", out var fileExtensions) && fileExtensions is TomlArray extArray)
            torrent.FileExtensionAllowlist = extArray.Select(x => x?.ToString() ?? "").ToList();

        if (table.TryGetValue("AutoDelete", out var autoDelete))
            torrent.AutoDelete = Convert.ToBoolean(autoDelete);

        if (table.TryGetValue("IgnoreTorrentsYoungerThan", out var ignoreYounger))
            torrent.IgnoreTorrentsYoungerThan = DurationParser.ParseToSeconds(ignoreYounger, 180);

        if (table.TryGetValue("MaximumETA", out var maxEta))
            torrent.MaximumETA = DurationParser.ParseToSeconds(maxEta);

        if (table.TryGetValue("MaximumDeletablePercentage", out var maxDeletablePct))
            torrent.MaximumDeletablePercentage = Convert.ToDouble(maxDeletablePct);

        if (table.TryGetValue("DoNotRemoveSlow", out var doNotRemoveSlow))
            torrent.DoNotRemoveSlow = Convert.ToBoolean(doNotRemoveSlow);

        if (table.TryGetValue("StalledDelay", out var stalledDelay))
            torrent.StalledDelay = DurationParser.ParseToMinutes(stalledDelay, 15);

        if (table.TryGetValue("ReSearchStalled", out var reSearchStalled))
            torrent.ReSearchStalled = Convert.ToBoolean(reSearchStalled);

        // Some configs (e.g. Lidarr) place these directly on Torrent rather than Torrent.SeedingMode
        if (table.TryGetValue("RemoveDeadTrackers", out var removeDeadTrackers))
            torrent.RemoveDeadTrackers = Convert.ToBoolean(removeDeadTrackers);

        if (table.TryGetValue("RemoveTrackerWithMessage", out var removeMsgs) && removeMsgs is TomlArray removeMsgArray)
            torrent.RemoveTrackerWithMessage = removeMsgArray.Select(x => x?.ToString() ?? "").ToList();

        // Parse [[Arr.Torrent.Trackers]] array-of-tables
        if (table.TryGetValue("Trackers", out var trackersObj) && trackersObj is TomlArray trackersArray)
            torrent.Trackers = trackersArray.Select(t => ParseTrackerConfig(t as TomlTable)).Where(t => t != null).ToList()!;

        // Parse SeedingMode section
        if (table.TryGetValue("SeedingMode", out var seedingModeObj) && seedingModeObj is TomlTable seedingModeTable)
        {
            torrent.SeedingMode = ParseSeedingModeConfig(seedingModeTable);
        }

        return torrent;
    }

    private SeedingModeConfig ParseSeedingModeConfig(TomlTable table)
    {
        var seeding = new SeedingModeConfig();

        if (table.TryGetValue("DownloadRateLimitPerTorrent", out var downloadLimit))
            seeding.DownloadRateLimitPerTorrent = Convert.ToInt32(downloadLimit);

        if (table.TryGetValue("UploadRateLimitPerTorrent", out var uploadLimit))
            seeding.UploadRateLimitPerTorrent = Convert.ToInt32(uploadLimit);

        if (table.TryGetValue("MaxUploadRatio", out var maxRatio))
            seeding.MaxUploadRatio = Convert.ToDouble(maxRatio);

        if (table.TryGetValue("MaxSeedingTime", out var maxTime))
            seeding.MaxSeedingTime = DurationParser.ParseToSeconds(maxTime);

        if (table.TryGetValue("RemoveTorrent", out var removeTorrent))
            seeding.RemoveTorrent = Convert.ToInt32(removeTorrent);

        if (table.TryGetValue("RemoveDeadTrackers", out var removeDeadTrackers))
            seeding.RemoveDeadTrackers = Convert.ToBoolean(removeDeadTrackers);

        if (table.TryGetValue("RemoveTrackerWithMessage", out var removeMessages) && removeMessages is TomlArray msgArray)
            seeding.RemoveTrackerWithMessage = msgArray.Select(x => x?.ToString() ?? "").ToList();

        // HnR settings are now tracker-only (removed from SeedingMode in v5.9.1)
        // These fields are still parsed for backwards compatibility with old configs

        return seeding;
    }

    private SearchConfig ParseSearchConfig(TomlTable table, string arrType)
    {
        var search = new SearchConfig();

        if (table.TryGetValue("SearchMissing", out var searchMissing))
            search.SearchMissing = Convert.ToBoolean(searchMissing);

        if (table.TryGetValue("AlsoSearchSpecials", out var searchSpecials))
            search.AlsoSearchSpecials = Convert.ToBoolean(searchSpecials);

        if (table.TryGetValue("Unmonitored", out var unmonitored))
            search.Unmonitored = Convert.ToBoolean(unmonitored);

        if (table.TryGetValue("SearchLimit", out var searchLimit))
            search.SearchLimit = Convert.ToInt32(searchLimit);

        if (table.TryGetValue("SearchByYear", out var searchByYear))
            search.SearchByYear = Convert.ToBoolean(searchByYear);

        if (table.TryGetValue("SearchInReverse", out var searchInReverse))
            search.SearchInReverse = Convert.ToBoolean(searchInReverse);

        if (table.TryGetValue("SearchRequestsEvery", out var searchRequestsEvery))
            search.SearchRequestsEvery = DurationParser.ParseToSeconds(searchRequestsEvery, 300);

        if (table.TryGetValue("DoUpgradeSearch", out var doUpgradeSearch))
            search.DoUpgradeSearch = Convert.ToBoolean(doUpgradeSearch);

        if (table.TryGetValue("QualityUnmetSearch", out var qualityUnmetSearch))
            search.QualityUnmetSearch = Convert.ToBoolean(qualityUnmetSearch);

        if (table.TryGetValue("CustomFormatUnmetSearch", out var customFormatUnmetSearch))
            search.CustomFormatUnmetSearch = Convert.ToBoolean(customFormatUnmetSearch);

        if (table.TryGetValue("ForceMinimumCustomFormat", out var forceMinCustomFormat))
            search.ForceMinimumCustomFormat = Convert.ToBoolean(forceMinCustomFormat);

        if (table.TryGetValue("SearchAgainOnSearchCompletion", out var searchAgainOnCompletion))
            search.SearchAgainOnSearchCompletion = Convert.ToBoolean(searchAgainOnCompletion);

        if (table.TryGetValue("UseTempForMissing", out var useTempForMissing))
            search.UseTempForMissing = Convert.ToBoolean(useTempForMissing);

        if (table.TryGetValue("KeepTempProfile", out var keepTempProfile))
            search.KeepTempProfile = Convert.ToBoolean(keepTempProfile);

        if (table.TryGetValue("QualityProfileMappings", out var profileMappings) && profileMappings is TomlTable mappingsTable)
        {
            search.QualityProfileMappings = new Dictionary<string, string>();
            foreach (var mapping in mappingsTable)
            {
                search.QualityProfileMappings[mapping.Key] = mapping.Value?.ToString() ?? "";
            }
        }

        if (table.TryGetValue("ForceResetTempProfiles", out var forceResetTempProfiles))
            search.ForceResetTempProfiles = Convert.ToBoolean(forceResetTempProfiles);

        if (table.TryGetValue("TempProfileResetTimeoutMinutes", out var tempProfileResetTimeout))
            search.TempProfileResetTimeoutMinutes = DurationParser.ParseToMinutes(tempProfileResetTimeout, 0);

        if (table.TryGetValue("ProfileSwitchRetryAttempts", out var profileSwitchRetryAttempts))
            search.ProfileSwitchRetryAttempts = Convert.ToInt32(profileSwitchRetryAttempts);

        if (table.TryGetValue("MainQualityProfile", out var mainProfiles) && mainProfiles is TomlArray mainArray)
            search.MainQualityProfile = mainArray.Select(x => x?.ToString() ?? "").ToList();

        if (table.TryGetValue("TempQualityProfile", out var tempProfiles) && tempProfiles is TomlArray tempArray)
            search.TempQualityProfile = tempArray.Select(x => x?.ToString() ?? "").ToList();

        // Sonarr-specific options
        if (arrType == "sonarr")
        {
            if (table.TryGetValue("SearchBySeries", out var searchBySeries))
                search.SearchBySeries = searchBySeries?.ToString() ?? "smart";

            if (table.TryGetValue("PrioritizeTodaysReleases", out var prioritizeTodaysReleases))
                search.PrioritizeTodaysReleases = Convert.ToBoolean(prioritizeTodaysReleases);
        }

        // Parse Ombi section
        if (table.TryGetValue("Ombi", out var ombiObj) && ombiObj is TomlTable ombiTable)
        {
            search.Ombi = ParseOmbiConfig(ombiTable);
        }

        // Parse Overseerr section
        if (table.TryGetValue("Overseerr", out var overseerrObj) && overseerrObj is TomlTable overseerrTable)
        {
            search.Overseerr = ParseOverseerrConfig(overseerrTable);
        }

        return search;
    }

    private OmbiConfig ParseOmbiConfig(TomlTable table)
    {
        var ombi = new OmbiConfig();

        if (table.TryGetValue("SearchOmbiRequests", out var searchOmbiRequests))
            ombi.SearchOmbiRequests = Convert.ToBoolean(searchOmbiRequests);

        if (table.TryGetValue("OmbiURI", out var ombiUri))
            ombi.OmbiURI = ombiUri?.ToString() ?? "";

        if (table.TryGetValue("OmbiAPIKey", out var ombiApiKey))
            ombi.OmbiAPIKey = ombiApiKey?.ToString() ?? "";

        if (table.TryGetValue("ApprovedOnly", out var approvedOnly))
            ombi.ApprovedOnly = Convert.ToBoolean(approvedOnly);

        return ombi;
    }

    private OverseerrConfig ParseOverseerrConfig(TomlTable table)
    {
        var overseerr = new OverseerrConfig();

        if (table.TryGetValue("SearchOverseerrRequests", out var searchOverseerrRequests))
            overseerr.SearchOverseerrRequests = Convert.ToBoolean(searchOverseerrRequests);

        if (table.TryGetValue("OverseerrURI", out var overseerrUri))
            overseerr.OverseerrURI = overseerrUri?.ToString() ?? "";

        if (table.TryGetValue("OverseerrAPIKey", out var overseerrApiKey))
            overseerr.OverseerrAPIKey = overseerrApiKey?.ToString() ?? "";

        if (table.TryGetValue("ApprovedOnly", out var approvedOnly))
            overseerr.ApprovedOnly = Convert.ToBoolean(approvedOnly);

        if (table.TryGetValue("Is4K", out var is4K))
            overseerr.Is4K = Convert.ToBoolean(is4K);

        return overseerr;
    }

    /// <summary>
    /// Load configuration, creating a default if not found
    /// </summary>
    public (TorrentarrConfig Config, bool CreatedDefault) LoadOrCreate()
    {
        if (!File.Exists(_configPath))
        {
            var config = GenerateDefaultConfig();
            SaveConfig(config, _configPath);
            return (config, true);
        }

        var config2 = Load();
        var (isValid, message, currentVersion) = ValidateConfigVersion(config2);
        if (message == "migration_needed")
        {
            config2.Settings.ConfigVersion = ExpectedConfigVersion;
            SaveConfig(config2, _configPath);
        }
        return (config2, false);
    }

    /// <summary>
    /// Validates config version against expected. Used for GET /web/config warning and optional migration.
    /// </summary>
    /// <returns>(isValid, message, currentVersion). isValid is true if current &lt;= expected; message is set when mismatch (newer) or "migration_needed" (older).</returns>
    public static (bool IsValid, string? Message, string CurrentVersion) ValidateConfigVersion(TorrentarrConfig config)
    {
        var currentStr = config.Settings?.ConfigVersion ?? "0.0.1";
        if (!Version.TryParse(currentStr, out var current))
            current = new Version(0, 0, 1);
        if (!Version.TryParse(ExpectedConfigVersion, out var expected))
            expected = new Version(5, 9, 2);

        if (current == expected)
            return (true, null, currentStr);
        if (current < expected)
            return (true, "migration_needed", currentStr);
        return (false,
            $"Config version mismatch: found {currentStr}, expected {ExpectedConfigVersion}. Your config may have been created with a newer version and may not work correctly. Please update Torrentarr or restore a compatible config backup.",
            currentStr);
    }

    /// <summary>
    /// Generate default configuration matching qBitrr's gen_config.py
    /// </summary>
    public static TorrentarrConfig GenerateDefaultConfig()
    {
        return new TorrentarrConfig
        {
            Settings = new SettingsConfig
            {
                ConfigVersion = "5.9.2",
                ConsoleLevel = "INFO",
                Logging = true,
                CompletedDownloadFolder = "CHANGE_ME",
                FreeSpace = "-1",
                FreeSpaceFolder = "CHANGE_ME",
                AutoPauseResume = true,
                NoInternetSleepTimer = 15,
                LoopSleepTimer = 5,
                SearchLoopDelay = -1,
                FailedCategory = "failed",
                RecheckCategory = "recheck",
                Tagless = false,
                IgnoreTorrentsYoungerThan = 180,
                PingURLS = new List<string> { "one.one.one.one", "dns.google.com" },
                FFprobeAutoUpdate = true,
                AutoUpdateEnabled = false,
                AutoUpdateCron = "0 3 * * 0",
                AutoRestartProcesses = true,
                MaxProcessRestarts = 5,
                ProcessRestartWindow = 300,
                ProcessRestartDelay = 5
            },
            // QBit is intentionally omitted from default config — user adds it via WebUI
            WebUI = new WebUIConfig
            {
                Host = "0.0.0.0",
                Port = 6969,
                Token = "",
                LiveArr = true,
                GroupSonarr = true,
                GroupLidarr = true,
                Theme = "Dark",
                ViewDensity = "Comfortable"
            },
            ArrInstances = GenerateDefaultArrInstances()
        };
    }

    private static Dictionary<string, ArrInstanceConfig> GenerateDefaultArrInstances()
    {
        // Return empty dictionary on fresh install.
        // Users add Arr instances (Sonarr-TV, Radarr-1080, etc.) via the WebUI
        // or by editing config.toml manually.
        return new Dictionary<string, ArrInstanceConfig>();
    }

    /// <summary>
    /// Save configuration to TOML file
    /// </summary>
    public void SaveConfig(TorrentarrConfig config, string? path = null)
    {
        var filePath = path ?? _configPath;
        var directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tomlContent = GenerateTomlContent(config);
        File.WriteAllText(filePath, tomlContent);
    }

    private string GenerateTomlContent(TorrentarrConfig config)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# This is a config file for Torrentarr (qBitrr compatible)");
        sb.AppendLine("# Make sure to change all entries of \"CHANGE_ME\"");
        sb.AppendLine();

        // Settings section
        sb.AppendLine("[Settings]");
        sb.AppendLine($"ConfigVersion = \"{config.Settings.ConfigVersion}\"");
        sb.AppendLine($"ConsoleLevel = \"{config.Settings.ConsoleLevel}\"");
        sb.AppendLine($"Logging = {config.Settings.Logging.ToString().ToLower()}");
        sb.AppendLine($"CompletedDownloadFolder = \"{config.Settings.CompletedDownloadFolder}\"");
        sb.AppendLine($"FreeSpace = \"{config.Settings.FreeSpace}\"");
        sb.AppendLine($"FreeSpaceFolder = \"{config.Settings.FreeSpaceFolder}\"");
        sb.AppendLine($"AutoPauseResume = {config.Settings.AutoPauseResume.ToString().ToLower()}");
        sb.AppendLine($"NoInternetSleepTimer = {config.Settings.NoInternetSleepTimer}");
        sb.AppendLine($"LoopSleepTimer = {config.Settings.LoopSleepTimer}");
        sb.AppendLine($"SearchLoopDelay = {config.Settings.SearchLoopDelay}");
        sb.AppendLine($"FailedCategory = \"{config.Settings.FailedCategory}\"");
        sb.AppendLine($"RecheckCategory = \"{config.Settings.RecheckCategory}\"");
        sb.AppendLine($"Tagless = {config.Settings.Tagless.ToString().ToLower()}");
        sb.AppendLine($"IgnoreTorrentsYoungerThan = {config.Settings.IgnoreTorrentsYoungerThan}");
        sb.AppendLine($"PingURLS = [{string.Join(", ", config.Settings.PingURLS.Select(u => $"\"{u}\""))}]");
        sb.AppendLine($"FFprobeAutoUpdate = {config.Settings.FFprobeAutoUpdate.ToString().ToLower()}");
        sb.AppendLine($"AutoUpdateEnabled = {config.Settings.AutoUpdateEnabled.ToString().ToLower()}");
        sb.AppendLine($"AutoUpdateCron = \"{config.Settings.AutoUpdateCron}\"");
        sb.AppendLine($"AutoRestartProcesses = {config.Settings.AutoRestartProcesses.ToString().ToLower()}");
        sb.AppendLine($"MaxProcessRestarts = {config.Settings.MaxProcessRestarts}");
        sb.AppendLine($"ProcessRestartWindow = {config.Settings.ProcessRestartWindow}");
        sb.AppendLine($"ProcessRestartDelay = {config.Settings.ProcessRestartDelay}");
        sb.AppendLine();

        // WebUI section
        sb.AppendLine("[WebUI]");
        sb.AppendLine($"Host = \"{config.WebUI.Host}\"");
        sb.AppendLine($"Port = {config.WebUI.Port}");
        sb.AppendLine($"Token = \"{config.WebUI.Token}\"");
        sb.AppendLine($"LiveArr = {config.WebUI.LiveArr.ToString().ToLower()}");
        sb.AppendLine($"GroupSonarr = {config.WebUI.GroupSonarr.ToString().ToLower()}");
        sb.AppendLine($"GroupLidarr = {config.WebUI.GroupLidarr.ToString().ToLower()}");
        sb.AppendLine($"Theme = \"{config.WebUI.Theme}\"");
        sb.AppendLine($"ViewDensity = \"{config.WebUI.ViewDensity}\"");
        sb.AppendLine();

        // All qBit instances — "qBit" written first (primary), then additional [qBit-XXX]
        var orderedQbit = config.QBitInstances
            .Where(kv => !string.IsNullOrEmpty(kv.Value.Host) && kv.Value.Host != "CHANGE_ME")
            .OrderBy(kv => kv.Key == "qBit" ? 0 : 1).ThenBy(kv => kv.Key);

        foreach (var (name, qbit) in orderedQbit)
        {
            sb.AppendLine($"[{name}]");
            sb.AppendLine($"Disabled = {qbit.Disabled.ToString().ToLower()}");
            sb.AppendLine($"Host = \"{qbit.Host}\"");
            sb.AppendLine($"Port = {qbit.Port}");
            sb.AppendLine($"UserName = \"{qbit.UserName}\"");
            sb.AppendLine($"Password = \"{qbit.Password}\"");
            sb.AppendLine($"v5 = {qbit.V5.ToString().ToLower()}");
            if (!string.IsNullOrEmpty(qbit.DownloadPath))
                sb.AppendLine($"DownloadPath = \"{qbit.DownloadPath}\"");
            sb.AppendLine($"ManagedCategories = [{string.Join(", ", qbit.ManagedCategories.Select(c => $"\"{c}\""))}]");
            if (name == "qBit")
            {
                sb.AppendLine("# Shared tracker configs inherited by all Arr instances on this qBit instance.");
                sb.AppendLine("# Add tracker entries here to configure per-tracker rate limits, HnR protection, etc.");
                sb.AppendLine("# Example: [[qBit.Trackers]]");
                sb.AppendLine("#          URI = \"tracker.example.com\"");
                sb.AppendLine("#          Priority = 1");
            }
            sb.AppendLine("Trackers = []");
            sb.AppendLine();

            sb.AppendLine($"[{name}.CategorySeeding]");
            sb.AppendLine($"DownloadRateLimitPerTorrent = {qbit.CategorySeeding.DownloadRateLimitPerTorrent}");
            sb.AppendLine($"UploadRateLimitPerTorrent = {qbit.CategorySeeding.UploadRateLimitPerTorrent}");
            sb.AppendLine($"MaxUploadRatio = {qbit.CategorySeeding.MaxUploadRatio}");
            sb.AppendLine($"MaxSeedingTime = {qbit.CategorySeeding.MaxSeedingTime}");
            sb.AppendLine($"RemoveTorrent = {qbit.CategorySeeding.RemoveTorrent}");
            sb.AppendLine($"HitAndRunMode = \"{qbit.CategorySeeding.HitAndRunMode}\"");
            sb.AppendLine($"MinSeedRatio = {qbit.CategorySeeding.MinSeedRatio}");
            sb.AppendLine($"MinSeedingTimeDays = {qbit.CategorySeeding.MinSeedingTimeDays}");
            sb.AppendLine($"HitAndRunMinimumDownloadPercent = {qbit.CategorySeeding.HitAndRunMinimumDownloadPercent}");
            sb.AppendLine($"HitAndRunPartialSeedRatio = {qbit.CategorySeeding.HitAndRunPartialSeedRatio}");
            sb.AppendLine($"TrackerUpdateBuffer = {qbit.CategorySeeding.TrackerUpdateBuffer}");
            sb.AppendLine($"StalledDelay = {qbit.CategorySeeding.StalledDelay}");
            sb.AppendLine($"IgnoreTorrentsYoungerThan = {qbit.CategorySeeding.IgnoreTorrentsYoungerThan}");
            sb.AppendLine();
        }

        // Arr instances
        foreach (var kvp in config.ArrInstances)
        {
            var instance = kvp.Value;
            sb.AppendLine($"[{kvp.Key}]");
            sb.AppendLine($"Managed = {instance.Managed.ToString().ToLower()}");
            sb.AppendLine($"URI = \"{instance.URI}\"");
            sb.AppendLine($"APIKey = \"{instance.APIKey}\"");
            sb.AppendLine($"Category = \"{instance.Category}\"");
            sb.AppendLine($"ReSearch = {instance.ReSearch.ToString().ToLower()}");
            sb.AppendLine($"importMode = \"{instance.ImportMode}\"");
            sb.AppendLine($"RssSyncTimer = {instance.RssSyncTimer}");
            sb.AppendLine($"RefreshDownloadsTimer = {instance.RefreshDownloadsTimer}");
            sb.AppendLine($"ArrErrorCodesToBlocklist = [{string.Join(", ", instance.ArrErrorCodesToBlocklist.Select(e => $"\"{EscapeTomlString(e)}\""))}]");
            sb.AppendLine();

            // Torrent section
            sb.AppendLine($"[{kvp.Key}.Torrent]");
            sb.AppendLine($"CaseSensitiveMatches = {instance.Torrent.CaseSensitiveMatches.ToString().ToLower()}");
            sb.AppendLine($"FolderExclusionRegex = [{string.Join(", ", instance.Torrent.FolderExclusionRegex.Select(r => $"'{r}'"))}]");
            sb.AppendLine($"FileNameExclusionRegex = [{string.Join(", ", instance.Torrent.FileNameExclusionRegex.Select(r => $"'{r}'"))}]");
            // Use TOML literal strings (single-quoted) for file extensions so backslashes
            // in patterns like '\.r[0-9]{2}' are preserved verbatim without escape processing.
            sb.AppendLine($"FileExtensionAllowlist = [{string.Join(", ", instance.Torrent.FileExtensionAllowlist.Select(e => $"'{e}'"))}]");
            sb.AppendLine($"AutoDelete = {instance.Torrent.AutoDelete.ToString().ToLower()}");
            sb.AppendLine($"IgnoreTorrentsYoungerThan = {instance.Torrent.IgnoreTorrentsYoungerThan}");
            sb.AppendLine($"MaximumETA = {instance.Torrent.MaximumETA}");
            sb.AppendLine($"MaximumDeletablePercentage = {instance.Torrent.MaximumDeletablePercentage}");
            sb.AppendLine($"DoNotRemoveSlow = {instance.Torrent.DoNotRemoveSlow.ToString().ToLower()}");
            sb.AppendLine($"StalledDelay = {instance.Torrent.StalledDelay}");
            sb.AppendLine($"ReSearchStalled = {instance.Torrent.ReSearchStalled.ToString().ToLower()}");
            sb.AppendLine();

            // Torrent.SeedingMode section
            var sm = instance.Torrent.SeedingMode ?? new SeedingModeConfig();
            sb.AppendLine($"[{kvp.Key}.Torrent.SeedingMode]");
            sb.AppendLine($"DownloadRateLimitPerTorrent = {sm.DownloadRateLimitPerTorrent}");
            sb.AppendLine($"UploadRateLimitPerTorrent = {sm.UploadRateLimitPerTorrent}");
            sb.AppendLine($"MaxUploadRatio = {sm.MaxUploadRatio}");
            sb.AppendLine($"MaxSeedingTime = {sm.MaxSeedingTime}");
            sb.AppendLine($"RemoveTorrent = {sm.RemoveTorrent}");
            sb.AppendLine($"RemoveDeadTrackers = {sm.RemoveDeadTrackers.ToString().ToLower()}");
            sb.AppendLine($"RemoveTrackerWithMessage = [{string.Join(", ", sm.RemoveTrackerWithMessage.Select(m => $"\"{EscapeTomlString(m)}\""))}]");
            // HnR settings are now tracker-only (removed from SeedingMode in v5.9.1)
            sb.AppendLine();

            // [[Arr.Torrent.Trackers]] array-of-tables
            foreach (var tracker in instance.Torrent.Trackers)
            {
                sb.AppendLine($"[[{kvp.Key}.Torrent.Trackers]]");
                if (!string.IsNullOrEmpty(tracker.Name))
                    sb.AppendLine($"Name = \"{EscapeTomlString(tracker.Name)}\"");
                sb.AppendLine($"URI = \"{tracker.Uri}\"");
                sb.AppendLine($"Priority = {tracker.Priority}");
                sb.AppendLine($"MaximumETA = {tracker.MaxETA ?? -1}");
                sb.AppendLine($"DownloadRateLimit = {tracker.DownloadRateLimit ?? -1}");
                sb.AppendLine($"UploadRateLimit = {tracker.UploadRateLimit ?? -1}");
                sb.AppendLine($"MaxUploadRatio = {tracker.MaxUploadRatio ?? -1}");
                sb.AppendLine($"MaxSeedingTime = {tracker.MaxSeedingTime ?? -1}");
                sb.AppendLine($"HitAndRunMode = \"{tracker.HitAndRunMode ?? "disabled"}\"");
                sb.AppendLine($"MinSeedRatio = {tracker.MinSeedRatio ?? 1.0}");
                sb.AppendLine($"MinSeedingTime = {tracker.MinSeedingTimeDays ?? 0}"); // TOML key is MinSeedingTime (qBitrr compat)
                sb.AppendLine($"HitAndRunPartialSeedRatio = {tracker.HitAndRunPartialSeedRatio ?? 1.0}");
                sb.AppendLine($"TrackerUpdateBuffer = {tracker.TrackerUpdateBuffer ?? 0}");
                sb.AppendLine($"HitAndRunMinimumDownloadPercent = {tracker.HitAndRunMinimumDownloadPercent ?? 10}");
                if (tracker.SuperSeedMode.HasValue)
                    sb.AppendLine($"SuperSeedMode = {tracker.SuperSeedMode.Value.ToString().ToLower()}");
                sb.AppendLine($"RemoveIfExists = {tracker.RemoveIfExists.ToString().ToLower()}");
                sb.AppendLine($"AddTrackerIfMissing = {tracker.AddTrackerIfMissing.ToString().ToLower()}");
                if (tracker.AddTags.Count > 0)
                    sb.AppendLine($"AddTags = [{string.Join(", ", tracker.AddTags.Select(t => $"'{t}'"))}]");
                sb.AppendLine();
            }

            // EntrySearch section
            sb.AppendLine($"[{kvp.Key}.EntrySearch]");
            sb.AppendLine($"SearchMissing = {instance.Search.SearchMissing.ToString().ToLower()}");
            if (instance.Type == "sonarr")
            {
                sb.AppendLine($"AlsoSearchSpecials = {instance.Search.AlsoSearchSpecials.ToString().ToLower()}");
            }
            sb.AppendLine($"Unmonitored = {instance.Search.Unmonitored.ToString().ToLower()}");
            sb.AppendLine($"SearchLimit = {instance.Search.SearchLimit}");
            if (instance.Type != "lidarr")
            {
                sb.AppendLine($"SearchByYear = {instance.Search.SearchByYear.ToString().ToLower()}");
            }
            sb.AppendLine($"SearchInReverse = {instance.Search.SearchInReverse.ToString().ToLower()}");
            sb.AppendLine($"SearchRequestsEvery = {instance.Search.SearchRequestsEvery}");
            sb.AppendLine($"DoUpgradeSearch = {instance.Search.DoUpgradeSearch.ToString().ToLower()}");
            sb.AppendLine($"QualityUnmetSearch = {instance.Search.QualityUnmetSearch.ToString().ToLower()}");
            sb.AppendLine($"CustomFormatUnmetSearch = {instance.Search.CustomFormatUnmetSearch.ToString().ToLower()}");
            sb.AppendLine($"ForceMinimumCustomFormat = {instance.Search.ForceMinimumCustomFormat.ToString().ToLower()}");
            sb.AppendLine($"SearchAgainOnSearchCompletion = {instance.Search.SearchAgainOnSearchCompletion.ToString().ToLower()}");
            sb.AppendLine($"UseTempForMissing = {instance.Search.UseTempForMissing.ToString().ToLower()}");
            sb.AppendLine($"KeepTempProfile = {instance.Search.KeepTempProfile.ToString().ToLower()}");
            if (instance.Search.QualityProfileMappings.Count > 0)
            {
                sb.AppendLine($"QualityProfileMappings = {{{string.Join(", ", instance.Search.QualityProfileMappings.Select(m => $"\"{m.Key}\" = \"{m.Value}\""))}}}");
            }
            sb.AppendLine($"ForceResetTempProfiles = {instance.Search.ForceResetTempProfiles.ToString().ToLower()}");
            sb.AppendLine($"TempProfileResetTimeoutMinutes = {instance.Search.TempProfileResetTimeoutMinutes}");
            sb.AppendLine($"ProfileSwitchRetryAttempts = {instance.Search.ProfileSwitchRetryAttempts}");
            if (instance.Type == "sonarr")
            {
                sb.AppendLine($"SearchBySeries = \"{instance.Search.SearchBySeries}\"");
                sb.AppendLine($"PrioritizeTodaysReleases = {instance.Search.PrioritizeTodaysReleases.ToString().ToLower()}");
            }

            // Ombi section (not for Lidarr)
            if (instance.Type != "lidarr")
            {
                var ombi = instance.Search.Ombi ?? new OmbiConfig();
                sb.AppendLine();
                sb.AppendLine($"[{kvp.Key}.EntrySearch.Ombi]");
                sb.AppendLine($"SearchOmbiRequests = {ombi.SearchOmbiRequests.ToString().ToLower()}");
                sb.AppendLine($"OmbiURI = \"{EscapeTomlString(string.IsNullOrEmpty(ombi.OmbiURI) ? "CHANGE_ME" : ombi.OmbiURI)}\"");
                sb.AppendLine($"OmbiAPIKey = \"{EscapeTomlString(string.IsNullOrEmpty(ombi.OmbiAPIKey) ? "CHANGE_ME" : ombi.OmbiAPIKey)}\"");
                sb.AppendLine($"ApprovedOnly = {ombi.ApprovedOnly.ToString().ToLower()}");

                var overseerr = instance.Search.Overseerr ?? new OverseerrConfig();
                sb.AppendLine();
                sb.AppendLine($"[{kvp.Key}.EntrySearch.Overseerr]");
                sb.AppendLine($"SearchOverseerrRequests = {overseerr.SearchOverseerrRequests.ToString().ToLower()}");
                sb.AppendLine($"OverseerrURI = \"{EscapeTomlString(string.IsNullOrEmpty(overseerr.OverseerrURI) ? "CHANGE_ME" : overseerr.OverseerrURI)}\"");
                sb.AppendLine($"OverseerrAPIKey = \"{EscapeTomlString(string.IsNullOrEmpty(overseerr.OverseerrAPIKey) ? "CHANGE_ME" : overseerr.OverseerrAPIKey)}\"");
                sb.AppendLine($"ApprovedOnly = {overseerr.ApprovedOnly.ToString().ToLower()}");
                sb.AppendLine($"Is4K = {overseerr.Is4K.ToString().ToLower()}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parse HitAndRunMode from TOML value — accepts string ("and"/"or"/"disabled") or legacy bool (true→"and", false→"disabled").
    /// </summary>
    private static string? ParseHitAndRunMode(object? value)
    {
        if (value is string s)
        {
            var lower = s.Trim().ToLowerInvariant();
            return lower is "and" or "or" or "disabled" ? lower : "disabled";
        }
        if (value is bool b)
            return b ? "and" : "disabled";
        // Try string conversion for other types
        var str = value?.ToString()?.Trim().ToLowerInvariant();
        if (str is "true") return "and";
        if (str is "false") return "disabled";
        return str is "and" or "or" or "disabled" ? str : "disabled";
    }

    /// <summary>
    /// Escape special characters for TOML string values
    /// </summary>
    private static string EscapeTomlString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
