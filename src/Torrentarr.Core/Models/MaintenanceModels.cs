namespace Torrentarr.Core.Models;

public enum MaintenanceOperation
{
    OrphanScan,
    HardlinkAudit,
    UnregisteredCleanup,
    CategoryReconcile,
    AutomaticManagement,
    PrivateTagging,
    TrackerErrorTagging,
    RepairPaused,
    RecycleRetention = 9 // 8 is retired; persisted history must keep its original meaning.
}

public enum MaintenanceActionKind
{
    QuarantineFile,
    AddTag,
    RemoveTag,
    SetCategory,
    SetAutomaticManagement,
    Recheck,
    Resume,
    // Retired action IDs stay unused for persisted-history compatibility.
    RecycleContent = 9,
    DeleteExpiredRecycleItem = 13
}

public sealed class MaintenanceAction
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public MaintenanceActionKind Kind { get; init; }
    public MaintenanceOperation Operation { get; set; }
    public string ClientInstanceId { get; init; } = "";
    public string? TorrentHash { get; init; }
    public string? TorrentName { get; init; }
    public string? Path { get; init; }
    public long Bytes { get; init; }
    public string Reason { get; init; } = "";
    public Dictionary<string, string> Arguments { get; init; } = new();
    public List<string> RequiredCapabilities { get; init; } = new();
    public bool Destructive { get; init; }
    public string? BlockedReason { get; set; }
}

public sealed class MaintenancePlan
{
    public string PlanId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; init; }
    public string ConfigurationFingerprint { get; init; } = "";
    public List<string> ClientInstanceIds { get; init; } = new();
    public List<MaintenanceOperation> Operations { get; init; } = new();
    public List<MaintenanceAction> Actions { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<string> CriticalErrors { get; init; } = new();
    public List<MaintenanceDiagnostic> Diagnostics { get; init; } = new();
    public List<MaintenanceOperationResult> OperationResults { get; init; } = new();
    public bool Consumed { get; set; }
    public int CandidateCount => Actions.Count(a => a.BlockedReason == null);
    public int BlockedCount => Actions.Count(a => a.BlockedReason != null);
    public long CandidateBytes => Actions.Where(a => a.BlockedReason == null).Sum(a => a.Bytes);
}

public sealed class MaintenancePreviewRequest
{
    public List<string> ClientInstanceIds { get; init; } = new();
    public List<MaintenanceOperation> Operations { get; init; } = new();
}

public sealed class MaintenanceApplyRequest
{
    public string PlanId { get; init; } = "";
}

public sealed class MaintenanceArmRequest
{
    public List<string> ClientInstanceIds { get; init; } = new();
    public string PlanId { get; init; } = "";
}

public sealed class MaintenanceClientsRequest
{
    public List<MaintenanceOperation> Operations { get; init; } = new();
    public List<string> ClientInstanceIds { get; init; } = new();
}

public sealed class MaintenanceRunSummary
{
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Trigger { get; init; } = "manual";
    public List<string> ClientInstanceIds { get; init; } = new();
    public int Planned { get; set; }
    public int Blocked { get; init; }
    public int Pending => Math.Max(0, Planned - Applied - Skipped - Failed);
    public List<MaintenanceOperationResult> OperationResults { get; init; } = new();
    public List<MaintenanceDiagnostic> Diagnostics { get; init; } = new();
    public int Applied { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public long BytesMoved { get; set; }
    public string Result { get; set; } = "running";
    public List<string> Errors { get; init; } = new();
}

public sealed class MaintenanceStatus
{
    public bool Running { get; init; }
    public MaintenanceRunSummary? CurrentRun { get; init; }
    public MaintenanceRunSummary? LastRun { get; init; }
    public Dictionary<string, bool> Armed { get; init; } = new();
    public Dictionary<string, List<MaintenanceOperation>> ArmedOperations { get; init; } = new();
    public Dictionary<string, string?> NextScheduledRun { get; init; } = new();
}

public sealed class TorrentInventoryItem
{
    public required TorrentInfo Torrent { get; init; }
    public required TorrentClientView Client { get; init; }
    public List<TorrentFileInventoryItem> Files { get; init; } = new();
    public List<TorrentTracker> Trackers { get; init; } = new();
    public bool TrackersComplete { get; set; }
    public bool FilesComplete { get; set; }
    public bool LocalFilesComplete { get; set; }
}

public sealed class TorrentFileInventoryItem
{
    public required TorrentFileRecord File { get; init; }
    public required string ClientPath { get; init; }
    public string? LocalPath { get; init; }
}

public sealed record TorrentClientView(
    TorrentClientIdentity Identity,
    TorrentClientCapabilities Capabilities);

public sealed class TorrentInventory
{
    public bool OwnershipRequested { get; init; } = true;
    public bool OwnershipComplete { get; set; } = true;
    public bool IsComplete => OwnershipRequested && OwnershipComplete;
    public List<MaintenanceDiagnostic> Diagnostics { get; init; } = new();
    public HashSet<string> InspectedClients { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TorrentInventoryItem> Torrents { get; init; } = new();
    public Dictionary<string, List<TorrentInventoryItem>> LocalPathReferences { get; init; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public List<string> Warnings { get; init; } = new();
}

[Flags]
public enum InventoryRequirements
{
    None = 0,
    Metadata = 1,
    Trackers = 2,
    Files = 4,
    LocalFiles = 8,
    GlobalOwnership = 16,
    All = Metadata | Trackers | Files | LocalFiles | GlobalOwnership
}

public sealed class MaintenanceOperationResult
{
    public string ClientInstanceId { get; init; } = "";
    public MaintenanceOperation Operation { get; init; }
    public string State { get; set; } = "ready";
    public int Examined { get; set; }
    public int Excluded { get; set; }
    public int Uninspectable { get; set; }
    public int Actionable { get; set; }
    public int BlockedActions { get; set; }
    public string? Reason { get; set; }
}

public sealed record MaintenanceDiagnosticExample(string? TorrentHash, string? TorrentName, string? ClientPath, string? LocalPath);

public sealed class MaintenanceDiagnostic
{
    public string Code { get; init; } = "";
    public string Severity { get; init; } = "warning";
    public string ClientInstanceId { get; init; } = "";
    public List<MaintenanceOperation> Operations { get; set; } = new();
    public string Message { get; init; } = "";
    public string Remediation { get; init; } = "";
    public int AffectedFiles { get; set; }
    public int AffectedTorrents { get; set; }
    public List<MaintenanceDiagnosticExample> Examples { get; init; } = new();

    [System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public HashSet<string> TorrentHashes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static void Add(List<MaintenanceDiagnostic> diagnostics, string code, string client, string message,
        string remediation, MaintenanceDiagnosticExample? example = null, bool file = false,
        IEnumerable<MaintenanceOperation>? operations = null, string severity = "warning")
    {
        var diagnostic = diagnostics.FirstOrDefault(d => d.Code == code && d.ClientInstanceId.Equals(client, StringComparison.OrdinalIgnoreCase));
        if (diagnostic == null)
        {
            diagnostic = new() { Code = code, ClientInstanceId = client, Message = message, Remediation = remediation, Severity = severity };
            diagnostics.Add(diagnostic);
        }
        if (file) diagnostic.AffectedFiles++;
        if (example?.TorrentHash != null && diagnostic.TorrentHashes.Add(example.TorrentHash)) diagnostic.AffectedTorrents++;
        if (example != null && diagnostic.Examples.Count < 5 && !diagnostic.Examples.Contains(example)) diagnostic.Examples.Add(example);
        if (operations != null) diagnostic.Operations = diagnostic.Operations.Concat(operations).Distinct().ToList();
    }
}
