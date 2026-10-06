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
    SharePolicy,
    RecycleRetention
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
    Stop,
    RemoveTorrent,
    RecycleContent,
    DeleteContent,
    SetUploadLimit,
    EnableSuperSeeding,
    DeleteExpiredRecycleItem
}

public sealed class MaintenanceAction
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public MaintenanceActionKind Kind { get; init; }
    public string ClientInstanceId { get; init; } = "";
    public string? TorrentHash { get; init; }
    public string? TorrentName { get; init; }
    public string? Path { get; init; }
    public long Bytes { get; init; }
    public string Reason { get; init; } = "";
    public Dictionary<string, string> Arguments { get; init; } = new();
    public List<string> RequiredCapabilities { get; init; } = new();
    public bool Destructive { get; init; }
    public string? BlockedReason { get; init; }
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
    public Dictionary<string, string?> NextScheduledRun { get; init; } = new();
}

public sealed record MaintenanceEvent(
    string Type,
    DateTimeOffset Timestamp,
    string? ClientInstanceId,
    string Message,
    object? Data = null);

public sealed class TorrentInventoryItem
{
    public required TorrentInfo Torrent { get; init; }
    public required TorrentClientView Client { get; init; }
    public List<TorrentFileInventoryItem> Files { get; init; } = new();
    public List<TorrentTracker> Trackers { get; init; } = new();
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
    public List<TorrentInventoryItem> Torrents { get; init; } = new();
    public Dictionary<string, List<TorrentInventoryItem>> LocalPathReferences { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}
