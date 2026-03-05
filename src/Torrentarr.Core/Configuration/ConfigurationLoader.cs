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

    /// <summary>
    /// TEST USE ONLY. When set by test fixtures, GetDefaultConfigPath() returns this instead of env/defaults.
    /// Ensures the correct config is loaded when the host builds. Must never be set in production —
    /// doing so would redirect config loading to an arbitrary path and is a security/maintainability risk.
    /// </summary>
    public static string? TestConfigPathOverride { get; set; }

    private readonly string _configPath;

    public ConfigurationLoader(string? configPath = null)
    {
        _configPath = configPath ?? GetDefaultConfigPath();
    }

    public static string GetDefaultConfigPath()
    {
        if (!string.IsNullOrEmpty(TestConfigPathOverride))
            return TestConfigPathOverride;

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

        // Apply config migrations on the raw TOML table before parsing (qBitrr parity)
        var migrated = ApplyConfigMigrations(tomlTable);
        if (migrated)
        {
            try
            {
                // Write back the migrated TOML and create a backup
                var backupPath = _configPath + ".bak";
                if (File.Exists(_configPath))
                    File.Copy(_configPath, backupPath, overwrite: true);
                File.WriteAllText(_configPath, Toml.FromModel(tomlTable));
            }
            catch
            {
                // Migration save failed — continue with in-memory migrated table
            }
        }

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

        // Apply TORRENTARR_* environment variable overrides (qBitrr parity: QBITRR_* env vars)
        ApplyEnvironmentOverrides(config);

        return config;
    }

    /// <summary>
    /// Apply TORRENTARR_* environment variable overrides after TOML parsing.
    /// Matches qBitrr's QBITRR_SETTINGS_*, QBITRR_QBIT_*, QBITRR_OVERRIDES_* env vars.
    /// Only non-null env vars override the TOML-parsed values.
    /// </summary>
    private static void ApplyEnvironmentOverrides(TorrentarrConfig config)
    {
        // TORRENTARR_SETTINGS_* → config.Settings
        var s = config.Settings;
        ApplyEnvString("TORRENTARR_SETTINGS_CONSOLE_LEVEL", v => s.ConsoleLevel = v);
        ApplyEnvBool("TORRENTARR_SETTINGS_LOGGING", v => s.Logging = v);
        ApplyEnvString("TORRENTARR_SETTINGS_COMPLETED_DOWNLOAD_FOLDER", v => s.CompletedDownloadFolder = v);
        ApplyEnvString("TORRENTARR_SETTINGS_FREE_SPACE", v => s.FreeSpace = v);
        ApplyEnvString("TORRENTARR_SETTINGS_FREE_SPACE_FOLDER", v => s.FreeSpaceFolder = v);
        ApplyEnvInt("TORRENTARR_SETTINGS_NO_INTERNET_SLEEP_TIMER", v => s.NoInternetSleepTimer = v);
        ApplyEnvInt("TORRENTARR_SETTINGS_LOOP_SLEEP_TIMER", v => s.LoopSleepTimer = v);
        ApplyEnvInt("TORRENTARR_SETTINGS_SEARCH_LOOP_DELAY", v => s.SearchLoopDelay = v);
        ApplyEnvBool("TORRENTARR_SETTINGS_AUTO_PAUSE_RESUME", v => s.AutoPauseResume = v);
        ApplyEnvString("TORRENTARR_SETTINGS_FAILED_CATEGORY", v => s.FailedCategory = v);
        ApplyEnvString("TORRENTARR_SETTINGS_RECHECK_CATEGORY", v => s.RecheckCategory = v);
        ApplyEnvBool("TORRENTARR_SETTINGS_TAGLESS", v => s.Tagless = v);
        ApplyEnvInt("TORRENTARR_SETTINGS_IGNORE_TORRENTS_YOUNGER_THAN", v => s.IgnoreTorrentsYoungerThan = v);
        ApplyEnvBool("TORRENTARR_SETTINGS_FFPROBE_AUTO_UPDATE", v => s.FFprobeAutoUpdate = v);
        ApplyEnvBool("TORRENTARR_SETTINGS_AUTO_UPDATE_ENABLED", v => s.AutoUpdateEnabled = v);
        ApplyEnvString("TORRENTARR_SETTINGS_AUTO_UPDATE_CRON", v => s.AutoUpdateCron = v);
        ApplyEnvList("TORRENTARR_SETTINGS_PING_URLS", v => s.PingURLS = v);

        // TORRENTARR_QBIT_* → primary qBit instance (config.QBitInstances["qBit"])
        if (config.QBitInstances.TryGetValue("qBit", out var qbit))
        {
            ApplyEnvBool("TORRENTARR_QBIT_DISABLED", v => qbit.Disabled = v);
            ApplyEnvString("TORRENTARR_QBIT_HOST", v => qbit.Host = v);
            ApplyEnvInt("TORRENTARR_QBIT_PORT", v => qbit.Port = v);
            ApplyEnvString("TORRENTARR_QBIT_USERNAME", v => qbit.UserName = v);
            ApplyEnvString("TORRENTARR_QBIT_PASSWORD", v => qbit.Password = v);
        }
    }

    private static void ApplyEnvString(string envName, Action<string> setter)
    {
        var value = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(value))
            setter(value);
    }

    private static void ApplyEnvBool(string envName, Action<bool> setter)
    {
        var value = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrEmpty(value)) return;
        var lower = value.Trim().ToLowerInvariant();
        if (lower is "true" or "1" or "yes" or "on" or "y" or "t")
            setter(true);
        else if (lower is "false" or "0" or "no" or "off" or "n" or "f")
            setter(false);
    }

    private static void ApplyEnvInt(string envName, Action<int> setter)
    {
        var value = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(value) && int.TryParse(value, out var intVal))
            setter(intVal);
    }

    private static void ApplyEnvList(string envName, Action<List<string>> setter)
    {
        var value = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(value))
            setter(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
    }

    /// <summary>
    /// Apply config migrations on the raw TOML table before parsing into C# models.
    /// Matches qBitrr's apply_config_migrations() (WebUI migration, quality profile list→dict,
    /// HnR settings, category seeding defaults). Returns true if any changes were made.
    /// </summary>
    private static bool ApplyConfigMigrations(TomlTable root)
    {
        var changed = false;

        // Determine current config version
        var currentVersion = new Version(0, 0, 1);
        if (root.TryGetValue("Settings", out var settingsObj) && settingsObj is TomlTable settings)
        {
            if (settings.TryGetValue("ConfigVersion", out var verObj))
            {
                var verStr = verObj?.ToString() ?? "0.0.1";
                if (!Version.TryParse(verStr, out currentVersion!))
                    currentVersion = new Version(0, 0, 1);
            }
        }

        var expected = Version.Parse(ExpectedConfigVersion);
        if (currentVersion >= expected)
            return false; // Already current

        // Migration 1: Move WebUI Host/Port/Token from [Settings] to [WebUI]
        if (MigrateWebUIConfig(root))
            changed = true;

        // Migration 2: Convert MainQualityProfile/TempQualityProfile lists to QualityProfileMappings dict
        if (MigrateQualityProfileMappings(root))
            changed = true;

        // Migration 3: Add process restart settings defaults (< 0.0.3)
        if (currentVersion < new Version(0, 0, 3) && MigrateProcessRestartSettings(root))
            changed = true;

        // Migration 4: Add qBit category settings defaults (< 0.0.4)
        if (currentVersion < new Version(0, 0, 4) && MigrateQBitCategorySettings(root))
            changed = true;

        // Migration 5: Move HnR from SeedingMode to trackers, promote trackers to qBit level (< 5.8.8)
        if (currentVersion < new Version(5, 8, 8) && MigrateHnrSettings(root))
            changed = true;

        // Migration 6: Consolidate HitAndRunMode bool + HitAndRunClearMode → single string key
        if (MigrateHnrMode(root))
            changed = true;

        // Validate and fill missing config values with defaults
        if (ValidateAndFillConfig(root))
            changed = true;

        // Update ConfigVersion to current
        if (changed || currentVersion < expected)
        {
            if (!root.ContainsKey("Settings"))
                root["Settings"] = new TomlTable();
            if (root["Settings"] is TomlTable s)
            {
                s["ConfigVersion"] = ExpectedConfigVersion;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Migration: Move Host/Port/Token from [Settings] to [WebUI] if present in wrong section.
    /// </summary>
    private static bool MigrateWebUIConfig(TomlTable root)
    {
        if (!root.TryGetValue("Settings", out var sObj) || sObj is not TomlTable settings)
            return false;

        if (!root.ContainsKey("WebUI"))
            root["WebUI"] = new TomlTable();
        if (root["WebUI"] is not TomlTable webui)
            return false;

        var migrated = false;
        foreach (var key in new[] { "Host", "Port", "Token" })
        {
            if (settings.ContainsKey(key) && !webui.ContainsKey(key))
            {
                webui[key] = settings[key];
                settings.Remove(key);
                migrated = true;
            }
        }
        return migrated;
    }

    /// <summary>
    /// Migration: Convert MainQualityProfile + TempQualityProfile lists to QualityProfileMappings dict.
    /// </summary>
    private static bool MigrateQualityProfileMappings(TomlTable root)
    {
        var changed = false;
        foreach (var kvp in root)
        {
            if (!(kvp.Key.StartsWith("Radarr", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("Sonarr", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("Lidarr", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (kvp.Value is not TomlTable arrTable)
                continue;
            if (!arrTable.TryGetValue("EntrySearch", out var esObj) || esObj is not TomlTable entrySearch)
                continue;

            if (entrySearch.TryGetValue("MainQualityProfile", out var mainObj) &&
                entrySearch.TryGetValue("TempQualityProfile", out var tempObj) &&
                mainObj is TomlArray mainArr && tempObj is TomlArray tempArr &&
                mainArr.Count == tempArr.Count && mainArr.Count > 0)
            {
                var mappings = new TomlTable();
                for (var i = 0; i < mainArr.Count; i++)
                {
                    var mainName = mainArr[i]?.ToString()?.Trim() ?? "";
                    var tempName = tempArr[i]?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(mainName) && !string.IsNullOrEmpty(tempName))
                        mappings[mainName] = tempName;
                }
                if (mappings.Count > 0)
                {
                    entrySearch["QualityProfileMappings"] = mappings;
                    entrySearch.Remove("MainQualityProfile");
                    entrySearch.Remove("TempQualityProfile");
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// Migration: Convert bool HitAndRunMode values to string "and"/"or"/"disabled".
    /// qBitrr pre-5.9.2 used booleans; 5.9.2+ uses string.
    /// </summary>
    private static bool MigrateHnrMode(TomlTable root)
    {
        var changed = false;

        // Migrate in all [qBit*] CategorySeeding and Trackers sections
        foreach (var kvp in root)
        {
            if (!(kvp.Key.Equals("qBit", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (kvp.Value is not TomlTable qbitTable) continue;
            if (qbitTable.TryGetValue("CategorySeeding", out var csObj) && csObj is TomlTable catSeeding)
                if (MigrateHnrModeField(catSeeding)) changed = true;
            if (qbitTable.TryGetValue("Trackers", out var qtrObj))
                foreach (var trackerTable in GetTrackerTables(qtrObj))
                    if (MigrateHnrModeField(trackerTable))
                        changed = true;
        }

        // Migrate in all Arr Tracker sections
        foreach (var kvp in root)
        {
            if (!(kvp.Key.StartsWith("Radarr", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("Sonarr", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("Lidarr", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (kvp.Value is not TomlTable arrTable) continue;
            if (!arrTable.TryGetValue("Torrent", out var tObj) || tObj is not TomlTable torrentTable) continue;
            if (!torrentTable.TryGetValue("Trackers", out var trObj)) continue;
            foreach (var trackerTable in GetTrackerTables(trObj))
            {
                if (MigrateHnrModeField(trackerTable))
                    changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Get tracker tables from a TOML value that may be either a TomlArray or TomlTableArray.
    /// TOML [[...]] syntax creates TomlTableArray; programmatic creation uses TomlArray.
    /// </summary>
    private static IEnumerable<TomlTable> GetTrackerTables(object? trackersObj)
    {
        if (trackersObj is TomlTableArray tableArray)
        {
            foreach (var t in tableArray)
                yield return t;
        }
        else if (trackersObj is TomlArray array)
        {
            foreach (var item in array)
                if (item is TomlTable t)
                    yield return t;
        }
    }

    /// <summary>
    /// Resolve HitAndRunMode from a table, handling bool, string, and HitAndRunClearMode consolidation.
    /// </summary>
    private static string ResolveHnrMode(TomlTable table)
    {
        var validModes = new[] { "and", "or", "disabled" };

        // HitAndRunClearMode takes priority (newer key that supersedes bool HitAndRunMode)
        if (table.TryGetValue("HitAndRunClearMode", out var clearVal) && clearVal is string clearStr)
        {
            var normalized = clearStr.Trim().ToLowerInvariant();
            if (validModes.Contains(normalized))
                return normalized;
        }

        // Then check existing HitAndRunMode
        if (table.TryGetValue("HitAndRunMode", out var modeVal))
        {
            if (modeVal is string modeStr)
            {
                var normalized = modeStr.Trim().ToLowerInvariant();
                if (validModes.Contains(normalized))
                    return normalized;
            }
            if (modeVal is bool boolVal)
                return boolVal ? "and" : "disabled";
        }

        return "disabled";
    }

    private static bool MigrateHnrModeField(TomlTable table)
    {
        var hadClear = table.ContainsKey("HitAndRunClearMode");
        var hadBool = table.TryGetValue("HitAndRunMode", out var val) && val is bool;

        if (!hadClear && !hadBool) return false;

        var resolved = ResolveHnrMode(table);
        table["HitAndRunMode"] = resolved;
        if (hadClear)
            table.Remove("HitAndRunClearMode");
        return true;
    }

    /// <summary>
    /// Migration 3: Add process restart settings defaults to [Settings] if missing (qBitrr &lt; 0.0.3).
    /// </summary>
    private static bool MigrateProcessRestartSettings(TomlTable root)
    {
        if (!root.ContainsKey("Settings"))
            root["Settings"] = new TomlTable();
        if (root["Settings"] is not TomlTable settings)
            return false;

        var changed = false;
        if (!settings.ContainsKey("AutoRestartProcesses")) { settings["AutoRestartProcesses"] = true; changed = true; }
        if (!settings.ContainsKey("MaxProcessRestarts")) { settings["MaxProcessRestarts"] = (long)5; changed = true; }
        if (!settings.ContainsKey("ProcessRestartWindow")) { settings["ProcessRestartWindow"] = (long)300; changed = true; }
        if (!settings.ContainsKey("ProcessRestartDelay")) { settings["ProcessRestartDelay"] = (long)5; changed = true; }
        return changed;
    }

    /// <summary>
    /// Migration 4: Add ManagedCategories and CategorySeeding defaults to all qBit instances (qBitrr &lt; 0.0.4).
    /// </summary>
    private static bool MigrateQBitCategorySettings(TomlTable root)
    {
        var changed = false;
        foreach (var kvp in root)
        {
            if (!(kvp.Key.Equals("qBit", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (kvp.Value is not TomlTable qbitTable) continue;

            if (!qbitTable.ContainsKey("ManagedCategories"))
            {
                qbitTable["ManagedCategories"] = new TomlArray();
                changed = true;
            }

            if (!qbitTable.ContainsKey("CategorySeeding"))
            {
                var seeding = new TomlTable
                {
                    ["DownloadRateLimitPerTorrent"] = (long)(-1),
                    ["UploadRateLimitPerTorrent"] = (long)(-1),
                    ["MaxUploadRatio"] = -1.0,
                    ["MaxSeedingTime"] = (long)(-1),
                    ["RemoveTorrent"] = (long)(-1),
                    ["HitAndRunMode"] = "disabled",
                    ["MinSeedRatio"] = 1.0,
                    ["MinSeedingTimeDays"] = (long)0,
                    ["HitAndRunMinimumDownloadPercent"] = (long)10,
                    ["HitAndRunPartialSeedRatio"] = 1.0,
                    ["TrackerUpdateBuffer"] = (long)0
                };
                qbitTable["CategorySeeding"] = seeding;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Migration 5: Move HnR fields from Arr SeedingMode to trackers, promote Arr trackers to qBit level (qBitrr &lt; 5.8.8).
    /// </summary>
    private static bool MigrateHnrSettings(TomlTable root)
    {
        var changed = false;
        var hnrFields = new[] { "HitAndRunMode", "MinSeedRatio", "MinSeedingTimeDays",
            "HitAndRunMinimumDownloadPercent", "HitAndRunPartialSeedRatio", "TrackerUpdateBuffer" };
        var hnrDefaults = new Dictionary<string, object>
        {
            ["HitAndRunMode"] = "disabled",
            ["MinSeedRatio"] = 1.0,
            ["MinSeedingTimeDays"] = (long)0,
            ["HitAndRunMinimumDownloadPercent"] = (long)10,
            ["HitAndRunPartialSeedRatio"] = 1.0,
            ["TrackerUpdateBuffer"] = (long)0
        };

        // Step 1: Remove HnR fields from Arr SeedingMode sections + add to Arr tracker entries
        foreach (var kvp in root)
        {
            if (!(kvp.Key.StartsWith("Radarr", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("Sonarr", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("Lidarr", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (kvp.Value is not TomlTable arrTable) continue;
            if (!arrTable.TryGetValue("Torrent", out var tObj) || tObj is not TomlTable torrentTable) continue;

            // Remove HnR from SeedingMode
            if (torrentTable.TryGetValue("SeedingMode", out var smObj) && smObj is TomlTable seedingMode)
            {
                foreach (var field in hnrFields)
                {
                    if (seedingMode.ContainsKey(field))
                    {
                        seedingMode.Remove(field);
                        changed = true;
                    }
                }
            }

            // Add HnR defaults to each tracker entry
            if (torrentTable.TryGetValue("Trackers", out var trObj))
            {
                foreach (var trackerTable in GetTrackerTables(trObj))
                {
                    foreach (var (field, defaultVal) in hnrDefaults)
                    {
                        if (!trackerTable.ContainsKey(field))
                        {
                            trackerTable[field] = defaultVal;
                            changed = true;
                        }
                    }
                }
            }
        }

        // Step 2: Add HnR defaults to qBit CategorySeeding sections
        foreach (var kvp in root)
        {
            if (!(kvp.Key.Equals("qBit", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (kvp.Value is not TomlTable qbitTable) continue;
            if (!qbitTable.TryGetValue("CategorySeeding", out var csObj) || csObj is not TomlTable catSeeding) continue;

            foreach (var (field, defaultVal) in hnrDefaults)
            {
                if (!catSeeding.ContainsKey(field))
                {
                    catSeeding[field] = defaultVal;
                    changed = true;
                }
            }
        }

        // Step 3: Promote Arr-level trackers to qBit.Trackers (deduplicate by URI)
        foreach (var kvp in root)
        {
            if (!(kvp.Key.Equals("qBit", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (kvp.Value is not TomlTable qbitTable) continue;
            if (qbitTable.ContainsKey("Trackers")) continue; // Already has trackers

            // Collect trackers from all Arr instances, deduplicate by URI
            var promoted = new Dictionary<string, TomlTable>(StringComparer.OrdinalIgnoreCase);
            foreach (var arrKvp in root)
            {
                if (!(arrKvp.Key.StartsWith("Radarr", StringComparison.OrdinalIgnoreCase) ||
                      arrKvp.Key.StartsWith("Sonarr", StringComparison.OrdinalIgnoreCase) ||
                      arrKvp.Key.StartsWith("Lidarr", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (arrKvp.Value is not TomlTable arrTable) continue;
                if (!arrTable.TryGetValue("Torrent", out var tObj) || tObj is not TomlTable torrentTable) continue;
                if (!torrentTable.TryGetValue("Trackers", out var trObj)) continue;

                foreach (var trackerTable in GetTrackerTables(trObj))
                {
                    var uri = trackerTable.TryGetValue("URI", out var uriObj) ? uriObj?.ToString()?.Trim().TrimEnd('/') ?? "" : "";
                    if (!string.IsNullOrEmpty(uri) && !promoted.ContainsKey(uri))
                        promoted[uri] = trackerTable;
                }
            }

            if (promoted.Count > 0)
            {
                var trackerArray = new TomlArray();
                foreach (var t in promoted.Values)
                    trackerArray.Add(t);
                qbitTable["Trackers"] = trackerArray;
                changed = true;
            }
            else
            {
                qbitTable["Trackers"] = new TomlArray();
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Validate configuration and fill missing values with defaults. Normalize Theme/ViewDensity casing.
    /// Matches qBitrr's _validate_and_fill_config().
    /// </summary>
    private static bool ValidateAndFillConfig(TomlTable root)
    {
        var changed = false;

        // --- Settings defaults ---
        if (!root.ContainsKey("Settings"))
            root["Settings"] = new TomlTable();
        if (root["Settings"] is TomlTable settings)
        {
            var settingsDefaults = new (string Key, object Default)[]
            {
                ("ConsoleLevel", "INFO"),
                ("Logging", true),
                ("CompletedDownloadFolder", ""),
                ("FreeSpace", "-1"),
                ("FreeSpaceFolder", ""),
                ("AutoPauseResume", true),
                ("NoInternetSleepTimer", (long)15),
                ("LoopSleepTimer", (long)5),
                ("SearchLoopDelay", (long)(-1)),
                ("FailedCategory", "failed"),
                ("RecheckCategory", "recheck"),
                ("Tagless", false),
                ("IgnoreTorrentsYoungerThan", (long)180),
                ("FFprobeAutoUpdate", true),
                ("AutoUpdateEnabled", false),
                ("AutoUpdateCron", "0 3 * * 0"),
                ("AutoRestartProcesses", true),
                ("MaxProcessRestarts", (long)5),
                ("ProcessRestartWindow", (long)300),
                ("ProcessRestartDelay", (long)5),
            };
            foreach (var (key, defaultVal) in settingsDefaults)
            {
                if (!settings.ContainsKey(key))
                {
                    settings[key] = defaultVal;
                    changed = true;
                }
            }

            // PingURLS default (array)
            if (!settings.ContainsKey("PingURLS"))
            {
                var pingArr = new TomlArray { "one.one.one.one", "dns.google.com" };
                settings["PingURLS"] = pingArr;
                changed = true;
            }
        }

        // --- WebUI defaults ---
        if (!root.ContainsKey("WebUI"))
            root["WebUI"] = new TomlTable();
        if (root["WebUI"] is TomlTable webui)
        {
            var hasLegacyAuthMode = webui.ContainsKey("AuthMode");
            var webuiDefaults = new (string Key, object Default)[]
            {
                ("Host", "0.0.0.0"),
                ("Port", (long)6969),
                ("Token", ""),
                ("AuthDisabled", true),
                ("LocalAuthEnabled", false),
                ("OIDCEnabled", false),
                ("Username", ""),
                ("PasswordHash", ""),
                ("LiveArr", true),
                ("GroupSonarr", true),
                ("GroupLidarr", true),
                ("Theme", "Dark"),
                ("ViewDensity", "Comfortable"),
            };
            foreach (var (key, defaultVal) in webuiDefaults)
            {
                if (!webui.ContainsKey(key))
                {
                    if (hasLegacyAuthMode && (key == "AuthDisabled" || key == "LocalAuthEnabled" || key == "OIDCEnabled"))
                        continue;
                    webui[key] = defaultVal;
                    changed = true;
                }
            }

            // Normalize Theme casing
            if (webui.TryGetValue("Theme", out var themeVal))
            {
                var themeStr = themeVal?.ToString()?.Trim().ToLowerInvariant() ?? "dark";
                var normalized = themeStr == "light" ? "Light" : "Dark";
                if (themeVal?.ToString() != normalized)
                {
                    webui["Theme"] = normalized;
                    changed = true;
                }
            }

            // Normalize ViewDensity casing
            if (webui.TryGetValue("ViewDensity", out var densityVal))
            {
                var densityStr = densityVal?.ToString()?.Trim().ToLowerInvariant() ?? "comfortable";
                var normalized = densityStr == "compact" ? "Compact" : "Comfortable";
                if (densityVal?.ToString() != normalized)
                {
                    webui["ViewDensity"] = normalized;
                    changed = true;
                }
            }
        }

        // --- qBit defaults ---
        foreach (var kvp in root)
        {
            if (!(kvp.Key.Equals("qBit", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (kvp.Value is not TomlTable qbitTable) continue;

            var qbitDefaults = new (string Key, object Default)[]
            {
                ("Disabled", false),
                ("Host", "CHANGE_ME"),
                ("Port", (long)8080),
                ("UserName", "CHANGE_ME"),
                ("Password", "CHANGE_ME"),
            };
            foreach (var (key, defaultVal) in qbitDefaults)
            {
                if (!qbitTable.ContainsKey(key))
                {
                    qbitTable[key] = defaultVal;
                    changed = true;
                }
            }
        }

        // --- Arr EntrySearch defaults ---
        foreach (var kvp in root)
        {
            if (!(kvp.Key.StartsWith("Radarr", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("Sonarr", StringComparison.OrdinalIgnoreCase) ||
                  kvp.Key.StartsWith("Lidarr", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (kvp.Value is not TomlTable arrTable) continue;
            if (!arrTable.TryGetValue("EntrySearch", out var esObj) || esObj is not TomlTable entrySearch) continue;

            var esDefaults = new (string Key, object Default)[]
            {
                ("ForceResetTempProfiles", false),
                ("TempProfileResetTimeoutMinutes", (long)0),
                ("ProfileSwitchRetryAttempts", (long)3),
            };
            foreach (var (key, defaultVal) in esDefaults)
            {
                if (!entrySearch.ContainsKey(key))
                {
                    entrySearch[key] = defaultVal;
                    changed = true;
                }
            }

            // QualityProfileMappings as inline table
            if (!entrySearch.ContainsKey("QualityProfileMappings"))
            {
                entrySearch["QualityProfileMappings"] = new TomlTable();
                changed = true;
            }
        }

        // --- HnR defaults on CategorySeeding and Tracker sections ---
        var hnrDefaults = new Dictionary<string, object>
        {
            ["HitAndRunMode"] = "disabled",
            ["MinSeedRatio"] = 1.0,
            ["MinSeedingTimeDays"] = (long)0,
            ["HitAndRunMinimumDownloadPercent"] = (long)10,
            ["HitAndRunPartialSeedRatio"] = 1.0,
            ["TrackerUpdateBuffer"] = (long)0
        };

        foreach (var kvp in root)
        {
            if (kvp.Value is not TomlTable section) continue;

            // qBit.CategorySeeding
            if ((kvp.Key.Equals("qBit", StringComparison.OrdinalIgnoreCase) ||
                 kvp.Key.StartsWith("qBit-", StringComparison.OrdinalIgnoreCase)) &&
                section.TryGetValue("CategorySeeding", out var csObj) && csObj is TomlTable catSeeding)
            {
                foreach (var (field, defaultVal) in hnrDefaults)
                {
                    if (!catSeeding.ContainsKey(field))
                    {
                        catSeeding[field] = defaultVal;
                        changed = true;
                    }
                }
            }

            // qBit.Trackers
            if (section.TryGetValue("Trackers", out var trObj))
            {
                foreach (var trackerTable in GetTrackerTables(trObj))
                {
                    foreach (var (field, defaultVal) in hnrDefaults)
                    {
                        if (!trackerTable.ContainsKey(field))
                        {
                            trackerTable[field] = defaultVal;
                            changed = true;
                        }
                    }
                }
            }

            // Arr.Torrent.Trackers
            if (section.TryGetValue("Torrent", out var tObj) && tObj is TomlTable torrentTable &&
                torrentTable.TryGetValue("Trackers", out var atrObj))
            {
                foreach (var trackerTable in GetTrackerTables(atrObj))
                {
                    foreach (var (field, defaultVal) in hnrDefaults)
                    {
                        if (!trackerTable.ContainsKey(field))
                        {
                            trackerTable[field] = defaultVal;
                            changed = true;
                        }
                    }
                }
            }
        }

        return changed;
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

        if (table.TryGetValue("Trackers", out var trackersObj))
            qbit.Trackers = GetTrackerTables(trackersObj).Select(t => ParseTrackerConfig(t)).Where(t => t != null).ToList()!;

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

        bool hasNewAuthKeys = table.ContainsKey("AuthDisabled") ||
            table.ContainsKey("LocalAuthEnabled") ||
            table.ContainsKey("OIDCEnabled");

        if (hasNewAuthKeys)
        {
            if (table.TryGetValue("AuthDisabled", out var authDisabledVal))
                webui.AuthDisabled = Convert.ToBoolean(authDisabledVal);
            if (table.TryGetValue("LocalAuthEnabled", out var localAuthVal))
                webui.LocalAuthEnabled = Convert.ToBoolean(localAuthVal);
            if (table.TryGetValue("OIDCEnabled", out var oidcEnabledVal))
                webui.OIDCEnabled = Convert.ToBoolean(oidcEnabledVal);
        }
        else if (table.TryGetValue("AuthMode", out var authMode))
        {
            var mode = authMode?.ToString()?.Trim() ?? "Disabled";
            if (string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase))
            {
                webui.AuthDisabled = true;
                webui.LocalAuthEnabled = false;
                webui.OIDCEnabled = false;
            }
            else if (string.Equals(mode, "TokenOnly", StringComparison.OrdinalIgnoreCase))
            {
                // TokenOnly = require token for all access; auth required, no local/OIDC login
                webui.AuthDisabled = false;
                webui.LocalAuthEnabled = false;
                webui.OIDCEnabled = false;
            }
            else if (string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase))
            {
                webui.AuthDisabled = false;
                webui.LocalAuthEnabled = true;
                webui.OIDCEnabled = false;
            }
            else if (string.Equals(mode, "OIDC", StringComparison.OrdinalIgnoreCase))
            {
                webui.AuthDisabled = false;
                webui.LocalAuthEnabled = false;
                webui.OIDCEnabled = true;
            }
            else
            {
                webui.AuthDisabled = true;
                webui.LocalAuthEnabled = false;
                webui.OIDCEnabled = false;
            }
        }

        if (table.TryGetValue("Username", out var username))
            webui.Username = username?.ToString() ?? "";

        if (table.TryGetValue("PasswordHash", out var passwordHash))
            webui.PasswordHash = passwordHash?.ToString() ?? "";

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

        if (table.TryGetValue("OIDC", out var oidcObj) && oidcObj is TomlTable oidcTable)
            webui.OIDC = ParseOIDC(oidcTable);

        return webui;
    }

    private static OIDCConfig ParseOIDC(TomlTable table)
    {
        var oidc = new OIDCConfig();
        if (table.TryGetValue("Authority", out var v)) oidc.Authority = v?.ToString() ?? "";
        if (table.TryGetValue("ClientId", out v)) oidc.ClientId = v?.ToString() ?? "";
        if (table.TryGetValue("ClientSecret", out v)) oidc.ClientSecret = v?.ToString() ?? "";
        if (table.TryGetValue("Scopes", out v)) oidc.Scopes = v?.ToString() ?? "openid profile";
        if (table.TryGetValue("CallbackPath", out v)) oidc.CallbackPath = v?.ToString() ?? "/signin-oidc";
        if (table.TryGetValue("RequireHttpsMetadata", out v)) oidc.RequireHttpsMetadata = Convert.ToBoolean(v);
        return oidc;
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

        // Parse [[Arr.Torrent.Trackers]] array-of-tables (TomlTableArray from [[...]] or TomlArray from programmatic)
        if (table.TryGetValue("Trackers", out var trackersObj))
            torrent.Trackers = GetTrackerTables(trackersObj).Select(t => ParseTrackerConfig(t)).Where(t => t != null).ToList()!;

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
                AuthDisabled = true,
                LocalAuthEnabled = false,
                OIDCEnabled = false,
                Username = "",
                PasswordHash = "",
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
        sb.AppendLine($"AuthDisabled = {config.WebUI.AuthDisabled.ToString().ToLower()}");
        sb.AppendLine($"LocalAuthEnabled = {config.WebUI.LocalAuthEnabled.ToString().ToLower()}");
        sb.AppendLine($"OIDCEnabled = {config.WebUI.OIDCEnabled.ToString().ToLower()}");
        sb.AppendLine($"Username = \"{EscapeTomlString(config.WebUI.Username)}\"");
        sb.AppendLine($"PasswordHash = \"{EscapeTomlString(config.WebUI.PasswordHash)}\"");
        sb.AppendLine($"LiveArr = {config.WebUI.LiveArr.ToString().ToLower()}");
        sb.AppendLine($"GroupSonarr = {config.WebUI.GroupSonarr.ToString().ToLower()}");
        sb.AppendLine($"GroupLidarr = {config.WebUI.GroupLidarr.ToString().ToLower()}");
        sb.AppendLine($"Theme = \"{config.WebUI.Theme}\"");
        sb.AppendLine($"ViewDensity = \"{config.WebUI.ViewDensity}\"");
        if (config.WebUI.OIDC != null)
        {
            var o = config.WebUI.OIDC;
            sb.AppendLine();
            sb.AppendLine("[WebUI.OIDC]");
            sb.AppendLine($"Authority = \"{EscapeTomlString(o.Authority)}\"");
            sb.AppendLine($"ClientId = \"{EscapeTomlString(o.ClientId)}\"");
            sb.AppendLine($"ClientSecret = \"{EscapeTomlString(o.ClientSecret)}\"");
            sb.AppendLine($"Scopes = \"{EscapeTomlString(o.Scopes)}\"");
            sb.AppendLine($"CallbackPath = \"{EscapeTomlString(o.CallbackPath)}\"");
            sb.AppendLine($"RequireHttpsMetadata = {o.RequireHttpsMetadata.ToString().ToLower()}");
        }
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
