using System.Text.Json.Serialization;

namespace Torrentarr.Core.Configuration;

/// <summary>
/// qBitrr-derived registry with Torrentarr client settings, shared by the schema API and WebUI generation.
/// </summary>
public static class ConfigSchemaBuilder
{
    public static object Build() => new
    {
        version = 1,
        sections = Sections
    };

    public static IReadOnlyDictionary<string, IReadOnlyList<ConfigSchemaField>> Sections { get; } =
        new Dictionary<string, IReadOnlyList<ConfigSchemaField>>
        {
            ["Settings"] = Qbitrr5146ConfigSchema.Settings.Where(f => f.key is not ("CompletedDownloadFolder" or "FreeSpace" or "FreeSpaceFolder" or "AutoPauseResume" or "FreeSpaceThresholdGB")).Select(f => f.key == "ConfigVersion" ? f with { @default = ConfigurationLoader.ExpectedConfigVersion } : f).ToList(),
            ["WebUI"] = Qbitrr5146ConfigSchema.WebUI,
            ["qBit"] = TorrentClientFields().Where(f => f.key != "Type").Select(f => f with { section = "qBit" }).ToList(),
            ["TorrentClient"] = TorrentClientFields(),
            ["Arr"] = Qbitrr5146ConfigSchema.Arr.Select(ClarifyTrackerMessages).Select(ClarifySearchScope).ToList(),
            ["TrackerProtection"] = ProtectionFields().Select(f => f with { section = "TrackerProtection" }).ToList()
        };

    private static ConfigSchemaField ClarifySearchScope(ConfigSchemaField field)
    {
        if (field.key == "EntrySearch.AlsoSearchSpecials")
        {
            const string specials = "Include season-zero episodes only when this series has at least one monitored special. Unmonitored also controls whether other specials may be searched.";
            return field with { description = specials, comments = new[] { specials } };
        }
        if (field.key != "EntrySearch.SearchBySeries") return field;
        const string description = "Smart: search a series, then seasons, when more than half of in-scope aired episodes need searching (at least two); otherwise search episodes. Series/season/episode force that scope. Counts use enabled missing, quality and custom-format settings across all years. Series searches split into permitted seasons when needed to exclude specials. Legacy true/false mean series/episode. Broad searches may also upgrade other episodes under Sonarr's profiles.";
        return field with { label = "Search Mode", options = new[] { "smart", "series", "season", "episode" }, description = description, comments = new[] { description } };
    }

    private static ConfigSchemaField ClarifyTrackerMessages(ConfigSchemaField field)
    {
        var description = field.path.LastOrDefault() switch
        {
            "RemoveDeadTrackers" => "Legacy compatibility setting: true preserves and reports matching tracker failures instead of directly deleting torrents. Tracker rejection never authorizes torrent removal.",
            "RemoveTrackerWithMessage" => "Tracker-message matches may remove the tracker URL only when RemoveDeadTrackers is false and fresh protection permits it. Messages never directly authorize torrent/content deletion.",
            _ => null
        };
        return description == null ? field : field with { description = description, comments = new[] { description } };
    }

    private static IReadOnlyList<ConfigSchemaField> TorrentClientFields() =>
    [
        .. Qbitrr5146ConfigSchema.qBit
            .Where(field => field.key is not ("Disabled" or "Host" or "Port" or "UserName" or "Password" or "DownloadPath"))
            .Select(ClarifyTrackerMessages).Select(field => field with { section = "TorrentClient", applyLive = true, requiresRestart = false }),
        Field("Type", "text", "Client Type", "qbittorrent"),
        Field("Disabled", "checkbox", "Disabled", false),
        Field("Host", "text", "Host", "CHANGE_ME"),
        Field("Port", "number", "Port", 8080, minimum: 1, maximum: 65535),
        Field("UserName", "text", "Username", "CHANGE_ME"),
        Field("Password", "password", "Password", "CHANGE_ME", secure: true),
        Field("DownloadPath", "text", "Download path", "") with
        { description = "Default download root as seen by this torrent client. Existing category paths are preserved.", applyLive = true, requiresRestart = false },
        Field("FreeSpace", "collection", "Free-space monitors", Array.Empty<object>()) with
        {
            description = "Paths visible to Torrentarr. One manager shares a budget across all clients using the same filesystem, preserving the largest configured reserve.",
            applyLive = true, requiresRestart = false,
            itemFields = [
                Item("Path", "Torrentarr path", "Absolute local folder to monitor. Downloads are matched using this client's path mappings.", "/data/downloads"),
                Field("Enabled", "checkbox", "Enabled", false),
                Item("Limit", "Minimum free space", "Reserve in bytes or with K, M, G, T suffix. Disable using the checkbox.", "10G") with { @default = "10G" }
            ]
        },
        Field("Maintenance.Enabled", "checkbox", "Maintenance Enabled", false),
        Field("Maintenance.Scope", "select", "Maintenance Scope", "managed",
            options: ["managed", "all", "explicit"]),
        Field("Maintenance.Schedule", "text", "Maintenance Schedule", "*/15 * * * *"),
        Field("Maintenance.PlanTtlMinutes", "number", "Preview TTL (minutes)", 30, minimum: 1),
        Field("Maintenance.MinimumTorrentAgeMinutes", "number", "Minimum torrent age (minutes)", 10, minimum: 0),
        Field("Maintenance.ExplicitCategories", "tags", "Explicit categories", Array.Empty<string>()),
        Field("Maintenance.ExcludedCategories", "tags", "Excluded categories", Array.Empty<string>()),
        Field("Maintenance.ExcludedTags", "tags", "Excluded tags", Array.Empty<string>()),
        Field("Maintenance.Orphans.Enabled", "checkbox", "Enable orphan scan", false) with { description = "Moves unreferenced files into the recycle bin; requires complete ownership inspection and local path mappings." },
        Field("Maintenance.Orphans.MinimumAgeMinutes", "number", "Minimum orphan file age (minutes)", 1440, minimum: 0),
        Field("Maintenance.Orphans.MaxFilesPerRun", "number", "Maximum orphan files per run", 10, minimum: 0),
        Field("Maintenance.Orphans.ExcludePatterns", "tags", "Orphan exclusion patterns", Array.Empty<string>()),
        Field("Maintenance.Hardlinks.Enabled", "checkbox", "Enable hardlink audit", false),
        Field("Maintenance.Hardlinks.Tag", "text", "Missing hardlink tag", "noHL"),
        Field("Maintenance.Hardlinks.CountLinksInsideRoot", "checkbox", "Count links inside root (unsupported)", false) with { description = "Unsupported: leave disabled. Root-scoped hardlink inspection cannot currently run." },
        Field("Maintenance.Hardlinks.Categories", "tags", "Hardlink audit categories", Array.Empty<string>()),
        Field("Maintenance.Hardlinks.ExcludedTags", "tags", "Hardlink audit excluded tags", Array.Empty<string>()),
        Field("Maintenance.AutomaticManagement.Enabled", "checkbox", "Enable automatic management", false) with { description = "Enables client automatic management, which may relocate content according to category paths." },
        Field("Maintenance.AutomaticManagement.IgnoredTags", "tags", "Automatic management ignored tags", Array.Empty<string>()),
        Field("Maintenance.AutomaticManagement.PrivateTag", "text", "Private torrent tag", "") with { description = "Blank disables private tagging. Existing tags are not automatically removed." },
        Field("Maintenance.AutomaticManagement.TrackerErrorTag", "text", "Tracker error tag", "issue") with { description = "Blank disables tracker-error tagging. Existing tags are not automatically removed." },
        Field("Maintenance.Repair.Enabled", "checkbox", "Enable paused torrent repair", false) with { description = "Complete paused torrents are resumed, including intentionally paused torrents. Incomplete torrents are rechecked." },
        Field("Maintenance.Repair.MaxPerRun", "number", "Maximum repairs per run", 10, minimum: 0),
        Field("Maintenance.Unregistered.Enabled", "checkbox", "Enable unregistered cleanup", false),
        Field("Maintenance.Unregistered.GraceMinutes", "number", "Unregistered grace period (minutes)", 10, minimum: 0),
        Field("Maintenance.Unregistered.MaxPerTrackerPerRun", "number", "Maximum removals per tracker per run", 10, minimum: 0),
        Field("Maintenance.Unregistered.CompletedOnly", "checkbox", "Only clean up completed torrents", false),
        Field("Maintenance.Unregistered.Messages", "tags", "Unregistered tracker messages", new[] { "unregistered torrent", "torrent not registered", "torrent not found", "torrent not authorized" }),
        Field("Maintenance.RecycleBin.Enabled", "checkbox", "Enable recycle bin and retention", true),
        Field("Maintenance.RecycleBin.Path", "text", "Recycle bin path", "") with { description = "Absolute path visible to Torrentarr. Blank uses .torrentarr-recycle inside the first mapped local root." },
        Field("Maintenance.RecycleBin.RetentionDays", "number", "Recycle retention (days)", 30, minimum: 0) with { description = "Expired recycle files are permanently deleted after this many days." },
        Field("Maintenance.RecycleBin.MaxTorrentMovesPerRun", "number", "Maximum torrent moves per run", 10, minimum: 0),
        Field("Maintenance.RecycleBin.SplitByClient", "checkbox", "Separate recycle bin by client", true),
        Field("Maintenance.RecycleBin.SplitByCategory", "checkbox", "Separate recycle bin by category", false),
        Field("Maintenance.RecycleBin.ResumeDataPath", "text", "Resume data backup path", "") with { description = "Optional resume-data backup directory, used when the client supports export." },
        .. OperationPolicyFields(),
        .. RecoveryFields(),
        Field("Maintenance.RecycleBin.RetentionEnabled", "checkbox", "Enable expired-file deletion", true) with { description = "Independent of recycle storage. Off preserves recycled files indefinitely." },
        Field("Maintenance.RecycleBin.MaxFilesDeletedPerRun", "number", "Maximum recycle files deleted per run", 100, minimum: 0),
        Field("Maintenance.Orphans.ScanRoots", "tags", "Orphan scan roots", Array.Empty<string>()) with { description = "Explicit local roots to scan. Each must match a path mapping. Blank disables filesystem scanning." },
        Field("Maintenance.Orphans.MaxInspectedFilesPerRun", "number", "Maximum files inspected per scan", 10000, minimum: 1),
        Field("Maintenance.Hardlinks.MaxInspectedTorrentsPerRun", "number", "Maximum torrents inspected per hardlink audit", 1000, minimum: 1),
        Field("Maintenance.Hardlinks.RemoveClearedTag", "checkbox", "Remove cleared hardlink tags", false) with { description = "Remove only tags recorded as applied by Torrentarr, when additional hardlinks are found." },
        Field("Maintenance.AutomaticManagement.RemoveClearedTrackerErrorTag", "checkbox", "Remove cleared tracker-error tags", false) with { description = "Remove only Torrentarr-owned tags when a tracker is healthy again." },
        Field("Maintenance.Repair.ResumeCompleted", "checkbox", "Resume complete paused torrents", true),
        Field("Maintenance.Repair.RecheckIncomplete", "checkbox", "Recheck incomplete paused torrents", true),
        Collection("CategoryMappings", "Category mappings", "Assign categories by save path; the longest matching path wins.", [
            Item("ClientPath", "Save path", "Absolute path as seen by the torrent client.", "/downloads/movies"),
            Item("Category", "Category", "Target category.", "movies")
        ]),
        Collection("CategoryTransitions", "Category transitions", "Change completed torrents' categories after a delay.", [
            Item("From", "From category", "Current category.", "movies"),
            Item("To", "To category", "Target category.", "seed"),
            Field("DelayMinutes", "number", "Delay after completion (minutes)", 0, minimum: 0)
        ]),
        Collection("TrackerAssignments", "Tracker assignments", "Assign categories and tags when a tracker URL contains the configured text.", [
            Item("TrackerContains", "Tracker URL contains", "Case-insensitive tracker URL match.", "tracker.example.com"),
            Field("Category", "text", "Assigned category", ""),
            Field("Tags", "tags", "Assigned tags", Array.Empty<string>())
        ]),
        Collection("PathMappings", "Path mappings", "Translate torrent client paths into paths visible to Torrentarr. This does not mount folders or move files. The longest matching client prefix wins. Saved mappings apply at the next worker processing cycle.", [
            Item("ClientPath", "Torrent client path", "Folder as seen by the torrent client.", "/downloads"),
            Item("LocalPath", "Torrentarr path", "The same folder as seen by Torrentarr, including inside its Docker container.", "/data/downloads")
        ]),
    ];

    private static IEnumerable<ConfigSchemaField> OperationPolicyFields()
    {
        foreach (var operation in Enum.GetValues<Torrentarr.Core.Models.MaintenanceOperation>())
        {
            var prefix = "Maintenance.Operations." + operation + ".";
            yield return Field(prefix + "Enabled", "checkbox", "Enable operation", false) with { description = "Explicit operation override. Existing configs without an override retain their legacy enablement." };
            yield return Field(prefix + "ExecutionMode", "select", "Execution mode", "manual", options: ["manual", "automatic"]) with { description = "Manual applies a reviewed preview once. Automatic runs on the saved schedule and remains enabled after restart; no restart is required." };
            yield return Field(prefix + "Schedule", "text", "Operation schedule (UTC)", MaintenancePolicies.SuggestedSchedule(operation)) with { description = "Five-field cron expression in UTC. Blank inherits the client schedule." };
            yield return Field(prefix + "IncludedCategories", "tags", "Included categories", Array.Empty<string>()) with { description = "Blank inherits client-wide scope. Entries narrow that scope." };
            yield return Field(prefix + "ExcludedCategories", "tags", "Excluded categories", Array.Empty<string>());
            yield return Field(prefix + "ExcludedTags", "tags", "Excluded tags", Array.Empty<string>());
            yield return Field(prefix + "MaxActionsPerRun", "number", "Maximum actions per run", null, minimum: 0) with { description = "Blank preserves operation limits. Zero permits no mutations." };
        }
    }

    private static IEnumerable<ConfigSchemaField> RecoveryFields() => [
        Field("Maintenance.ErrorRecovery.Enabled", "checkbox", "Enable error recovery (legacy fallback)", false),
        Field("Maintenance.ErrorRecovery.Outcome", "select", "Outcome after failed repair", "keep", options: ["keep", "recycle"]),
        Field("Maintenance.ErrorRecovery.States", "tags", "Client error states", new[] { "error", "missingFiles" }),
        Field("Maintenance.ErrorRecovery.MaxRepairAttempts", "number", "Repair attempts per incident", 1, minimum: 1),
        Field("Maintenance.ErrorRecovery.MaxPerRun", "number", "Maximum new rechecks per run", 2, minimum: 0),
        Field("Maintenance.ErrorRecovery.RetryMinutes", "number", "Rejected recheck retry delay (minutes)", 10, minimum: 1),
        Field("Maintenance.ErrorRecovery.RecheckTimeoutMinutes", "number", "Recheck confirmation timeout (minutes)", 60, minimum: 1),
        Field("Maintenance.ErrorRecovery.SustainedFailureMinutes", "number", "Confirmed recurring error delay (minutes)", 1440, minimum: 1),
        Field("Maintenance.ErrorRecovery.HealthyResetMinutes", "number", "Observed healthy reset (minutes)", 60, minimum: 1)
    ];

    private static IEnumerable<ConfigSchemaField> ProtectionFields() => [
        Field("Policy", "select", "Tracker protection policy", "custom", options: ["custom", "torrentleech-power", "onlyencodes"]),
        Field("MinimumSeedHours", "number", "Minimum completed seed time (hours)", 48, minimum: 0, maximum: 876000),
        Field("MinimumRatio", "number", "Minimum ratio", 1.0, minimum: 0),
        Field("AllowClientRatioClearance", "checkbox", "Allow client-estimated ratio clearance", false) with { description = "Off by default: a client ratio does not prove tracker credit. OnlyEncodes never clears on ratio." },
        Field("PartialPolicy", "select", "Partial download policy", "preserve", options: ["preserve", "ratio"]),
        Field("DownloadThresholdPercent", "number", "Download obligation threshold (%)", 10, minimum: 0, maximum: 100),
        Field("ThresholdInclusive", "checkbox", "Include threshold boundary", true),
        Field("FulfillmentHours", "number", "Fulfillment window (hours)", null, minimum: 1, maximum: 876000) with { description = "Blank means no deadline, except OnlyEncodes uses an explicitly unconfirmed 168-hour warning window. A missed deadline never clears protection." },
        Field("DeadlineStart", "select", "Deadline starts at", "added", options: ["added", "completed"]),
        Field("AdditionalSeedHours", "number", "Extra seed-time margin (hours)", 6, minimum: 0, maximum: 876000),
        Field("AccountingGraceMinutes", "number", "Accounting grace (minutes)", 60, minimum: 0, maximum: 52560000),
        Field("RecoveryReserveHours", "number", "Recovery reserve (hours)", 6, minimum: 0, maximum: 876000)
    ];

    private static ConfigSchemaField Collection(string name, string label, string description, IReadOnlyList<ConfigSchemaField> items)
        => Field("Maintenance." + name, "collection", label, Array.Empty<object>()) with
        { description = description, itemFields = items, applyLive = true, requiresRestart = false };

    private static ConfigSchemaField Item(string key, string label, string description, string example)
        => Field(key, "text", label, "") with
        { description = description, required = true, placeholder = example, applyLive = null, requiresRestart = null };

    private static string Describe(string key, string kind, string label) => key switch
    {
        "Type" => "Client adapter identifier. qbittorrent is shipped; other products require a registered factory.",
        "Host" => "Torrent client's WebUI address, reachable from Torrentarr's process/container.",
        "Port" => "Torrent client's WebUI TCP port, from 1 to 65535.",
        "UserName" => "Login username for the torrent client.",
        "Password" => "Client login secret. Redacted values retain the stored credential until explicitly replaced.",
        "Disabled" => "Disconnect and stop managing this client. Unknown remote file ownership still blocks destructive maintenance.",
        "Maintenance.PlanTtlMinutes" => "Positive minutes a saved preview remains executable. Saving or relevant revision changes invalidate it earlier.",
        "Maintenance.MinimumTorrentAgeMinutes" => "Minutes after addition before eligibility. Zero removes this extra delay; ordinary torrent-age grace still applies.",
        "Maintenance.Orphans.MinimumAgeMinutes" => "Preserve files modified within this many minutes. Default 1440 (24 hours); zero removes only this file-age delay.",
        "Maintenance.Hardlinks.MaxInspectedTorrentsPerRun" => "Positive inspection budget, default 1000 torrents. Applied runs persist a cursor for eventual coverage.",
        "Maintenance.Orphans.MaxInspectedFilesPerRun" => "Positive file eligibility-inspection budget, default 10000. Directory name ordering still enumerates the root.",
        "Maintenance.ErrorRecovery.MaxRepairAttempts" => "Positive accepted recheck attempts per incident. Default one; rejected submissions do not consume an accepted attempt.",
        "Maintenance.ErrorRecovery.RetryMinutes" => "Positive delay before retrying rejected rechecks. Default 10 minutes; failures never prove recovery.",
        "Maintenance.ErrorRecovery.RecheckTimeoutMinutes" => "Positive time allowed to observe checking and completion. Default 60 minutes; unconfirmed/timed-out checks never allow cleanup.",
        "Maintenance.ErrorRecovery.SustainedFailureMinutes" => "Positive continuously observed failure window after confirmed repair. Default 1440 minutes; observation gaps restart it.",
        "Maintenance.ErrorRecovery.HealthyResetMinutes" => "Positive observed-health window before resetting an incident. Default 60 minutes; unobserved downtime is not evidence.",
        "Maintenance.RecycleBin.RetentionDays" => "Days before permanent recycle deletion. Zero makes files immediately age-eligible; disable retention to preserve indefinitely.",
        "Maintenance.Enabled" => "Enable Manage tools. Select Automatic per tool to allow scheduled execution; saving never executes a tool immediately.",
        "Maintenance.Scope" => "Managed categories, all torrents, or an explicit category list. Operation filters can only narrow this scope.",
        "Maintenance.Schedule" => "Fallback five-field cron schedule in UTC, used only by enabled tools saved in Automatic mode.",
        "Maintenance.ExplicitCategories" => "Categories selected when scope is explicit. Empty selects none in explicit scope.",
        "Maintenance.ExcludedCategories" => "Always preserve torrents in these categories; exclusions override inclusion.",
        "Maintenance.ExcludedTags" => "Always preserve torrents bearing any of these tags.",
        "Maintenance.Orphans.ExcludePatterns" => "Wildcard patterns matched against relative paths, before any orphan move. Empty excludes no extra files.",
        "Maintenance.Hardlinks.Tag" => "Tag applied when a file has no additional hardlink. Requires filesystem link-count support.",
        "Maintenance.ErrorRecovery.Outcome" => "Keep reports recurring failures without deletion. Recycle permits removal only after confirmed repair, sustained failure, and all protection checks.",
        "Maintenance.ErrorRecovery.States" => "Exact client error-state names eligible for repair. Tracker messages alone never interrupt healthy protected seeding.",
        "Maintenance.Repair.ResumeCompleted" => "Explicitly authorizes resuming complete paused torrents, including deliberate user pauses. Review before enabling.",
        "Maintenance.Repair.RecheckIncomplete" => "Request file rechecks for incomplete paused torrents. This can create disk IO; it does not prove successful recovery.",
        "Maintenance.RecycleBin.Enabled" => "Allow local recycle storage for content moves. Retention deletion has a separate enablement and operation policy.",
        _ => label + (kind == "number" && key.Contains("Max", StringComparison.Ordinal) ? ". Zero permits no mutations for action/deletion caps; inspection budgets must be positive."
            : kind == "tags" ? ". Empty applies no additional list entries; operation filters inherit client scope."
            : kind == "checkbox" ? ". Off disables this behavior; operation enablement and authorization still apply."
            : ". Uses the displayed units and default; operation prerequisites and protection checks still apply.")
    };

    private static ConfigSchemaField Field(
        string key,
        string kind,
        string label,
        object? defaultValue,
        bool secure = false,
        IReadOnlyList<string>? options = null,
        double? minimum = null,
        double? maximum = null) => new(
            "TorrentClient",
            key.Split('.'),
            key,
            label,
            kind,
            defaultValue,
            Describe(key, kind, label),
            required: false,
            secure,
            uiExpose: true,
            applyLive: true,
            requiresRestart: false,
            options: options,
            minimum: minimum,
            maximum: maximum);
}

public sealed record ConfigSchemaField(
    string section,
    IReadOnlyList<string> path,
    string key,
    string label,
    string kind,
    object? @default,
    object comments,
    bool required,
    bool secure,
    bool uiExpose,
    bool? applyLive,
    bool? requiresRestart,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? options = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? placeholder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? nativeUnit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool allowNegative = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? minimum = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? maximum = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? arrKinds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ConfigSchemaField>? itemFields = null);
