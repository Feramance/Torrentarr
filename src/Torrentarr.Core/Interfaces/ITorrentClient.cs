using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;

namespace Torrentarr.Core.Interfaces;

public interface ITorrentClient
{
    TorrentClientIdentity Identity { get; }
    TorrentClientCapabilities Capabilities { get; }
    string? LastConnectionFailure { get; }

    Task<bool> LoginAsync(CancellationToken ct = default);
    Task<string> GetVersionAsync(CancellationToken ct = default);
    Task<List<TorrentInfo>> GetTorrentsAsync(string? category = null, string? sort = null, CancellationToken cancellationToken = default);
    Task<bool> AddTorrentAsync(string url, string? category = null, string? savePath = null, CancellationToken ct = default);
    Task<bool> DeleteTorrentsAsync(List<string> hashes, bool deleteFiles = false, CancellationToken ct = default);
    Task<bool> PauseTorrentsAsync(List<string> hashes, CancellationToken ct = default);
    Task<bool> ResumeTorrentsAsync(List<string> hashes, CancellationToken ct = default);
    Task<bool> PauseTorrentAsync(string hash, CancellationToken ct = default);
    Task<bool> ResumeTorrentAsync(string hash, CancellationToken ct = default);
    Task<bool> SetCategoryAsync(List<string> hashes, string category, CancellationToken ct = default);
    Task<Dictionary<string, TorrentClientCategory>> GetCategoriesAsync(CancellationToken ct = default);
    Task<bool> AddTagsAsync(List<string> hashes, List<string> tags, CancellationToken ct = default);
    Task<bool> RemoveTagsAsync(List<string> hashes, List<string> tags, CancellationToken ct = default);
    Task<bool> CreateTagsAsync(List<string> tags, CancellationToken ct = default);
    Task<List<string>> GetTagsAsync(CancellationToken ct = default);
    Task<List<TorrentTracker>> GetTorrentTrackersAsync(string hash, CancellationToken ct = default);
    Task<TorrentPropertiesRecord?> GetTorrentPropertiesAsync(string hash, CancellationToken ct = default);
    Task<List<TorrentFileRecord>> GetTorrentFilesAsync(string hash, CancellationToken ct = default);
    Task<bool> RecheckTorrentsAsync(List<string> hashes, CancellationToken ct = default);
    Task<bool> SetShareLimitsAsync(string hash, double ratioLimit, long seedingTimeLimit, CancellationToken ct = default);
    Task<bool> SetDownloadLimitAsync(string hash, long limit, CancellationToken ct = default);
    Task<bool> SetUploadLimitAsync(string hash, long limit, CancellationToken ct = default);
    Task<bool> SetSuperSeedingAsync(string hash, bool enabled, CancellationToken ct = default);
    Task<bool> SetAutomaticManagementAsync(List<string> hashes, bool enabled, CancellationToken ct = default);
    Task<bool> SetTopPriorityAsync(string hash, CancellationToken ct = default);
    Task<bool> AddTrackersAsync(string hash, List<string> urls, CancellationToken ct = default);
    Task<bool> RemoveTrackersAsync(string hash, List<string> urls, CancellationToken ct = default);
    Task<bool> SetFilePriorityAsync(string hash, int[] fileIds, int priority, CancellationToken ct = default);
    Task<bool> CreateCategoryAsync(string name, string? savePath = null, CancellationToken ct = default);
    Task<bool> EditCategoryAsync(string name, string savePath, CancellationToken ct = default);
    Task<bool> DeleteCategoryAsync(string name, CancellationToken ct = default);
    Task<TorrentTransferRecord?> GetTransferInfoAsync(CancellationToken ct = default);
    Task<TorrentClientSnapshot?> GetMainDataAsync(long? revision = null, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, byte[]>> ExportResumeDataAsync(string hash, CancellationToken ct = default);
}

public interface ITorrentClientFactory
{
    string Type { get; }
    ITorrentClient Create(string instanceId, TorrentClientInstanceConfig config);
}

public interface ITorrentClientRegistry
{
    Task<bool> InitializeAsync(string name, TorrentClientInstanceConfig config, CancellationToken ct = default);
    Task<int> EnsureAllConnectedAsync(IEnumerable<KeyValuePair<string, TorrentClientInstanceConfig>> instances, CancellationToken ct = default);
    ITorrentClient? GetClient(string instanceId);
    IReadOnlyDictionary<string, ITorrentClient> GetAllClients();
    bool IsConnected();
    bool IsConnected(string instanceId);
}
