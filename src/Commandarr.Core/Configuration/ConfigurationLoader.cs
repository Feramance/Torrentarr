using Tomlyn;
using Tomlyn.Model;

namespace Commandarr.Core.Configuration;

/// <summary>
/// Loads configuration from TOML files with backwards compatibility for qBitrr config.toml
/// </summary>
public class ConfigurationLoader
{
    private readonly string _configPath;

    public ConfigurationLoader(string? configPath = null)
    {
        _configPath = configPath ?? GetDefaultConfigPath();
    }

    public static string GetDefaultConfigPath()
    {
        // Try multiple locations (same as qBitrr)
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var possiblePaths = new[]
        {
            Path.Combine(homePath, "config", "config.toml"),
            Path.Combine(homePath, ".config", "qbitrr", "config.toml"),
            Path.Combine(homePath, ".config", "commandarr", "config.toml"),
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

    public CommandarrConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            throw new FileNotFoundException($"Configuration file not found at: {_configPath}");
        }

        var tomlContent = File.ReadAllText(_configPath);
        var tomlTable = Toml.ToModel(tomlContent);

        var config = new CommandarrConfig();

        // Parse Settings section
        if (tomlTable.TryGetValue("Settings", out var settingsObj) && settingsObj is TomlTable settingsTable)
        {
            config.Settings = ParseSettings(settingsTable);
        }

        // Parse qBit section
        if (tomlTable.TryGetValue("qBit", out var qbitObj) && qbitObj is TomlTable qbitTable)
        {
            config.QBit = ParseQBit(qbitTable);
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
            settings.ConfigVersion = configVersion?.ToString() ?? "5.8.8";

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
            settings.NoInternetSleepTimer = Convert.ToInt32(noInternetSleep);

        if (table.TryGetValue("LoopSleepTimer", out var loopSleep))
            settings.LoopSleepTimer = Convert.ToInt32(loopSleep);

        if (table.TryGetValue("SearchLoopDelay", out var searchDelay))
            settings.SearchLoopDelay = Convert.ToInt32(searchDelay);

        if (table.TryGetValue("FailedCategory", out var failedCat))
            settings.FailedCategory = failedCat?.ToString() ?? "failed";

        if (table.TryGetValue("RecheckCategory", out var recheckCat))
            settings.RecheckCategory = recheckCat?.ToString() ?? "recheck";

        if (table.TryGetValue("Tagless", out var tagless))
            settings.Tagless = Convert.ToBoolean(tagless);

        if (table.TryGetValue("IgnoreTorrentsYoungerThan", out var ignoreYounger))
            settings.IgnoreTorrentsYoungerThan = Convert.ToInt32(ignoreYounger);

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
            settings.ProcessRestartWindow = Convert.ToInt32(restartWindow);

        if (table.TryGetValue("ProcessRestartDelay", out var restartDelay))
            settings.ProcessRestartDelay = Convert.ToInt32(restartDelay);

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

        if (table.TryGetValue("ManagedCategories", out var categories) && categories is TomlArray catArray)
            qbit.ManagedCategories = catArray.Select(x => x?.ToString() ?? "").ToList();

        if (table.TryGetValue("CategorySeeding", out var seedingObj) && seedingObj is TomlTable seedingTable)
            qbit.CategorySeeding = ParseCategorySeeding(seedingTable);

        return qbit;
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
            seeding.MaxSeedingTime = Convert.ToInt32(maxTime);

        if (table.TryGetValue("RemoveMode", out var removeMode))
            seeding.RemoveMode = Convert.ToInt32(removeMode);

        if (table.TryGetValue("HitAndRunMode", out var hnrMode))
            seeding.HitAndRunMode = Convert.ToBoolean(hnrMode);

        if (table.TryGetValue("MinSeedRatio", out var minRatio))
            seeding.MinSeedRatio = Convert.ToDouble(minRatio);

        if (table.TryGetValue("MinSeedTime", out var minTime))
            seeding.MinSeedTime = Convert.ToInt32(minTime);

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

        // Look for sections matching Radarr-*, Sonarr-*, Lidarr-*
        foreach (var kvp in rootTable)
        {
            var sectionName = kvp.Key;
            if (kvp.Value is not TomlTable instanceTable)
                continue;

            if (sectionName.StartsWith("Radarr-", StringComparison.OrdinalIgnoreCase) ||
                sectionName.StartsWith("Sonarr-", StringComparison.OrdinalIgnoreCase) ||
                sectionName.StartsWith("Lidarr-", StringComparison.OrdinalIgnoreCase))
            {
                var instance = new ArrInstanceConfig();

                // Determine type from section name
                if (sectionName.StartsWith("Radarr-", StringComparison.OrdinalIgnoreCase))
                    instance.Type = "radarr";
                else if (sectionName.StartsWith("Sonarr-", StringComparison.OrdinalIgnoreCase))
                    instance.Type = "sonarr";
                else if (sectionName.StartsWith("Lidarr-", StringComparison.OrdinalIgnoreCase))
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

                instances[sectionName] = instance;
            }
        }

        return instances;
    }
}
