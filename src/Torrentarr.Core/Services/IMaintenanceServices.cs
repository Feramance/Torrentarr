using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;

namespace Torrentarr.Core.Services;

public interface IPathMappingService
{
    PathMappingResult Resolve(string clientPath, IReadOnlyList<PathMappingConfig> mappings);
    string? ValidateLocalPath(string path, string root, bool requireFile = false);
    string? MapToLocal(string clientPath, IReadOnlyList<PathMappingConfig> mappings);
    IReadOnlyList<string> Validate(IReadOnlyList<PathMappingConfig> mappings);
    bool IsSafeDescendant(string candidate, string root);
}

public interface ITorrentInventoryService
{
    Task<TorrentInventory> CollectAsync(IReadOnlyDictionary<string, InventoryRequirements> requirements, CancellationToken ct = default, string? candidateHash = null);
    Task<TorrentInventory> BuildAsync(IEnumerable<string>? instanceIds = null, CancellationToken ct = default);
    /// <summary>Refreshes one torrent for eligibility checks; does not establish global file ownership.</summary>
    Task<TorrentInventory> RefreshCandidateAsync(string instanceId, string hash, CancellationToken ct = default);
    Task<TorrentInventory> RefreshAsync(TorrentInventory previous, string? candidateInstance = null, string? candidateHash = null, CancellationToken ct = default);
}

public interface IHardlinkInspector
{
    Task<int?> GetLinkCountAsync(string path, CancellationToken ct = default);
}

public interface ISafeDeletionService
{
    Task<bool> DeleteOwnedFileAsync(string clientInstanceId, string hash, string path, CancellationToken ct = default) => Task.FromResult(false);
    Task<SafeDeletionResult> RemoveTorrentAsync(string clientInstanceId, string hash, bool removeContent, CancellationToken ct = default);
    /// <summary>Keep the original removal trigger across final HnR and content-safety checks.</summary>
    Task<SafeDeletionResult> RemoveTorrentForIntentAsync(string clientInstanceId, string hash, bool removeContent,
        TorrentRemovalIntent intent, CancellationToken ct = default)
        => Task.FromResult(new SafeDeletionResult(false, true, 0, "Intent-aware removal is unsupported by this deletion service."));
    Task<SafeDeletionResult> RemoveMaintenanceTorrentAsync(string clientInstanceId, string hash, bool removeContent, TorrentInventory inventory, CancellationToken ct = default);
    Task<SafeDeletionResult> QuarantineFileAsync(string clientInstanceId, string localPath, CancellationToken ct = default);
    IReadOnlyList<string> GetRecoveryPaths() => [];
    IReadOnlyList<RecycleEntry> InspectRecycleEntries(string clientInstanceId) => [];
    IReadOnlyList<RecycleFileSnapshot> InspectRecycleFiles(RecycleEntry entry) => [];
    Task<long> PurgeRecycleEntryAsync(string clientInstanceId, string key, IReadOnlyList<RecycleFileSnapshot> files, CancellationToken ct = default) => throw new NotSupportedException();
    void RegisterLegacyRecycleEntry(string clientInstanceId, string path) => throw new NotSupportedException();
    Task<int> CleanupExpiredAsync(string clientInstanceId, CancellationToken ct = default);
}

public sealed record SafeDeletionResult(bool Success, bool ContentPreserved, long BytesMoved, string? Error = null);

public interface IMaintenanceCoordinator
{
    MaintenanceStatus GetStatus();
    Task<MaintenancePlan> PreviewAsync(MaintenancePreviewRequest request, CancellationToken ct = default);
    Task<MaintenanceRunSummary> ApplyAsync(string planId, CancellationToken ct = default);
    Task<MaintenanceRunSummary> ApplyOnceAsync(string planId, CancellationToken ct = default);
    Task<MaintenanceRunSummary> RunAsync(IEnumerable<string>? instanceIds = null, string trigger = "manual", CancellationToken ct = default, IEnumerable<MaintenanceOperation>? operations = null);
    void Cancel();
    IReadOnlyList<MaintenanceRunSummary> GetHistory(int limit = 50);
}

public sealed record PathMappingResult(string? LocalPath, string? Code = null, string? Error = null, string? LocalRoot = null);
