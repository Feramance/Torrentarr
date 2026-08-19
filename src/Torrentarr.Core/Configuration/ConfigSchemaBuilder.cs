namespace Torrentarr.Core.Configuration;

/// <summary>qBitrr <c>GET /api/config/schema</c> field registry (labels, kinds, reload hints).</summary>
public static class ConfigSchemaBuilder
{
    public static object Build() => new
    {
        version = 1,
        sections = new Dictionary<string, object>
        {
            ["Settings"] = SettingsFields(),
            ["WebUI"] = WebUiFields(),
            ["qBit"] = QbitFields(),
            ["Arr"] = ArrFields()
        }
    };

    private static List<object> SettingsFields() =>
    [
        Field("ConfigVersion", "string", "Config Version", uiExpose: false),
        Field("ConsoleLevel", "select", "Console Level"),
        Field("Logging", "bool", "Logging"),
        Field("CompletedDownloadFolder", "string", "Completed Download Folder"),
        Field("FreeSpace", "string", "Free Space"),
        Field("FreeSpaceFolder", "string", "Free Space Folder"),
        Field("AutoPauseResume", "bool", "Auto Pause Resume"),
        Field("Tagless", "bool", "Tagless"),
        Field("FFprobeAutoUpdate", "bool", "FFprobe Auto Update"),
        Field("AutoUpdateEnabled", "bool", "Auto Update Enabled"),
        Field("AutoUpdateCron", "string", "Auto Update Cron"),
        Field("AutoUpdateChannel", "select", "Auto Update Channel"),
    ];

    private static List<object> WebUiFields() =>
    [
        Field("Host", "string", "Host"),
        Field("Port", "int", "Port"),
        Field("Token", "string", "Token", sensitive: true),
        Field("AuthDisabled", "bool", "Auth Disabled"),
        Field("LocalAuthEnabled", "bool", "Local Auth Enabled"),
        Field("AllowInsecureExposure", "bool", "Allow Insecure Exposure"),
        Field("AllowInsecureTokenQuery", "bool", "Allow Insecure Token Query"),
        Field("OIDCEnabled", "bool", "OIDC Enabled"),
        Field("UrlBase", "string", "URL Base"),
        Field("Theme", "select", "Theme"),
        Field("ViewDensity", "select", "View Density"),
    ];

    private static List<object> QbitFields() =>
    [
        Field("Disabled", "bool", "Disabled"),
        Field("Host", "string", "Host"),
        Field("Port", "int", "Port"),
        Field("UserName", "string", "Username"),
        Field("Password", "string", "Password", sensitive: true),
        Field("SkipTLSVerify", "bool", "Skip TLS Verify"),
        Field("CategorySeeding.StalledDelay", "duration", "Stalled Delay"),
        Field("CategorySeeding.MinSeedingTimeDays", "int", "Min Seeding Time (days)"),
    ];

    private static List<object> ArrFields() =>
    [
        Field("URI", "string", "URI"),
        Field("APIKey", "string", "API Key", sensitive: true),
        Field("SkipTLSVerify", "bool", "Skip TLS Verify"),
        Field("Managed", "bool", "Managed"),
        Field("Category", "string", "Category"),
        Field("ReSearch", "bool", "Re-Search"),
        Field("RssSyncTimer", "duration", "RSS Sync Timer"),
        Field("RefreshDownloadsTimer", "duration", "Refresh Downloads Timer"),
        Field("EntrySearch.SearchByYear", "bool", "Search By Year"),
        Field("EntrySearch.SearchMissing", "bool", "Search Missing"),
        Field("EntrySearch.KeepTempProfile", "bool", "Keep Temp Profile"),
        Field("EntrySearch.SearchAgainOnSearchCompletion", "bool", "Search Again On Completion"),
        Field("ArrErrorCodesToBlocklist", "list", "Arr Error Codes To Blocklist"),
    ];

    private static object Field(string dotted, string kind, string label, bool uiExpose = true, bool sensitive = false) => new
    {
        dotted,
        kind,
        label,
        uiExpose,
        sensitive
    };
}
