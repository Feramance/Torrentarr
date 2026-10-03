using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;

namespace Torrentarr.Core.Services;

public interface IPathMappingService
{
    string? MapToLocal(string clientPath, IReadOnlyList<PathMappingConfig> mappings);
    IReadOnlyList<string> Validate(IReadOnlyList<PathMappingConfig> mappings);
    bool IsSafeDescendant(string candidate, string root);
}

public interface ITorrentInventoryService
{
    Task<TorrentInventory> BuildAsync(IEnumerable<string>? instanceIds = null, CancellationToken ct = default);
}

public interface IHardlinkInspector
{
    Task<int?> GetLinkCountAsync(string path, CancellationToken ct = default);
}

public interface ISafeDeletionService
{
    Task<SafeDeletionResult> RemoveTorrentAsync(string clientInstanceId, string hash, bool removeContent, bool permanent = false, CancellationToken ct = default);
    Task<SafeDeletionResult> QuarantineFileAsync(string clientInstanceId, string localPath, CancellationToken ct = default);
    Task<int> CleanupExpiredAsync(string clientInstanceId, CancellationToken ct = default);
}

public sealed record SafeDeletionResult(bool Success, bool ContentPreserved, long BytesMoved, string? Error = null);

public interface IMaintenanceCoordinator
{
    MaintenanceStatus GetStatus();
    Task<MaintenancePlan> PreviewAsync(MaintenancePreviewRequest request, CancellationToken ct = default);
    Task<MaintenanceRunSummary> ApplyAsync(string planId, CancellationToken ct = default);
    Task ArmAsync(MaintenanceArmRequest request, CancellationToken ct = default);
    void Disarm(IEnumerable<string> instanceIds);
    Task<MaintenanceRunSummary> RunAsync(IEnumerable<string>? instanceIds = null, string trigger = "manual", CancellationToken ct = default);
    void Cancel();
    IReadOnlyList<MaintenanceRunSummary> GetHistory(int limit = 50);
}

public interface IMaintenanceNotificationService
{
    void Publish(MaintenanceEvent maintenanceEvent);
}
