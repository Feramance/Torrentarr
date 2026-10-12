using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;

namespace Torrentarr.Infrastructure.Services;

// Request leases let dispatched work finish while retired references reject new calls.
internal sealed class RegisteredTorrentClient(ITorrentClient client) : ITorrentClient, IDisposable
{
    private readonly object _gate = new();
    private int _active;
    private bool _retired;
    private bool _disposed;
    public TorrentClientIdentity Identity => client.Identity;
    public TorrentClientCapabilities Capabilities => client.Capabilities;
    public string? LastConnectionFailure => client.LastConnectionFailure;

    private async Task<T> UseAsync<T>(Func<Task<T>> request)
    {
        lock (_gate)
        {
            if (_retired) throw new ObjectDisposedException(nameof(RegisteredTorrentClient));
            _active++;
        }
        try { return await request(); }
        finally
        {
            lock (_gate)
            {
                _active--;
                DisposeIfIdle();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _retired = true;
            DisposeIfIdle();
        }
    }

    private void DisposeIfIdle()
    {
        if (!_retired || _active != 0 || _disposed) return;
        _disposed = true;
        (client as IDisposable)?.Dispose();
    }

    public Task<bool> LoginAsync(CancellationToken ct = default) => UseAsync(() => client.LoginAsync(ct));
    public Task<string> GetVersionAsync(CancellationToken ct = default) => UseAsync(() => client.GetVersionAsync(ct));
    public Task<List<TorrentInfo>> GetTorrentsAsync(string? category = null, string? sort = null, CancellationToken cancellationToken = default) => UseAsync(() => client.GetTorrentsAsync(category, sort, cancellationToken));
    public Task<bool> AddTorrentAsync(string url, string? category = null, string? savePath = null, CancellationToken ct = default) => UseAsync(() => client.AddTorrentAsync(url, category, savePath, ct));
    public Task<bool> DeleteTorrentsAsync(List<string> hashes, bool deleteFiles = false, CancellationToken ct = default) => UseAsync(() => client.DeleteTorrentsAsync(hashes, deleteFiles, ct));
    public Task<bool> PauseTorrentsAsync(List<string> hashes, CancellationToken ct = default) => UseAsync(() => client.PauseTorrentsAsync(hashes, ct));
    public Task<bool> ResumeTorrentsAsync(List<string> hashes, CancellationToken ct = default) => UseAsync(() => client.ResumeTorrentsAsync(hashes, ct));
    public Task<bool> PauseTorrentAsync(string hash, CancellationToken ct = default) => UseAsync(() => client.PauseTorrentAsync(hash, ct));
    public Task<bool> ResumeTorrentAsync(string hash, CancellationToken ct = default) => UseAsync(() => client.ResumeTorrentAsync(hash, ct));
    public Task<bool> SetCategoryAsync(List<string> hashes, string category, CancellationToken ct = default) => UseAsync(() => client.SetCategoryAsync(hashes, category, ct));
    public Task<Dictionary<string, TorrentClientCategory>> GetCategoriesAsync(CancellationToken ct = default) => UseAsync(() => client.GetCategoriesAsync(ct));
    public Task<bool> AddTagsAsync(List<string> hashes, List<string> tags, CancellationToken ct = default) => UseAsync(() => client.AddTagsAsync(hashes, tags, ct));
    public Task<bool> RemoveTagsAsync(List<string> hashes, List<string> tags, CancellationToken ct = default) => UseAsync(() => client.RemoveTagsAsync(hashes, tags, ct));
    public Task<bool> CreateTagsAsync(List<string> tags, CancellationToken ct = default) => UseAsync(() => client.CreateTagsAsync(tags, ct));
    public Task<List<string>> GetTagsAsync(CancellationToken ct = default) => UseAsync(() => client.GetTagsAsync(ct));
    public Task<List<TorrentTracker>> GetTorrentTrackersAsync(string hash, CancellationToken ct = default) => UseAsync(() => client.GetTorrentTrackersAsync(hash, ct));
    public Task<TorrentPropertiesRecord?> GetTorrentPropertiesAsync(string hash, CancellationToken ct = default) => UseAsync(() => client.GetTorrentPropertiesAsync(hash, ct));
    public Task<List<TorrentFileRecord>> GetTorrentFilesAsync(string hash, CancellationToken ct = default) => UseAsync(() => client.GetTorrentFilesAsync(hash, ct));
    public Task<bool> RecheckTorrentsAsync(List<string> hashes, CancellationToken ct = default) => UseAsync(() => client.RecheckTorrentsAsync(hashes, ct));
    public Task<bool> SetShareLimitsAsync(string hash, double ratioLimit, long seedingTimeLimit, CancellationToken ct = default) => UseAsync(() => client.SetShareLimitsAsync(hash, ratioLimit, seedingTimeLimit, ct));
    public Task<bool> SetDownloadLimitAsync(string hash, long limit, CancellationToken ct = default) => UseAsync(() => client.SetDownloadLimitAsync(hash, limit, ct));
    public Task<bool> SetUploadLimitAsync(string hash, long limit, CancellationToken ct = default) => UseAsync(() => client.SetUploadLimitAsync(hash, limit, ct));
    public Task<bool> SetSuperSeedingAsync(string hash, bool enabled, CancellationToken ct = default) => UseAsync(() => client.SetSuperSeedingAsync(hash, enabled, ct));
    public Task<bool> SetAutomaticManagementAsync(List<string> hashes, bool enabled, CancellationToken ct = default) => UseAsync(() => client.SetAutomaticManagementAsync(hashes, enabled, ct));
    public Task<bool> SetTopPriorityAsync(string hash, CancellationToken ct = default) => UseAsync(() => client.SetTopPriorityAsync(hash, ct));
    public Task<bool> AddTrackersAsync(string hash, List<string> urls, CancellationToken ct = default) => UseAsync(() => client.AddTrackersAsync(hash, urls, ct));
    public Task<bool> RemoveTrackersAsync(string hash, List<string> urls, CancellationToken ct = default) => UseAsync(() => client.RemoveTrackersAsync(hash, urls, ct));
    public Task<bool> SetFilePriorityAsync(string hash, int[] fileIds, int priority, CancellationToken ct = default) => UseAsync(() => client.SetFilePriorityAsync(hash, fileIds, priority, ct));
    public Task<bool> CreateCategoryAsync(string name, string? savePath = null, CancellationToken ct = default) => UseAsync(() => client.CreateCategoryAsync(name, savePath, ct));
    public Task<bool> EditCategoryAsync(string name, string savePath, CancellationToken ct = default) => UseAsync(() => client.EditCategoryAsync(name, savePath, ct));
    public Task<bool> DeleteCategoryAsync(string name, CancellationToken ct = default) => UseAsync(() => client.DeleteCategoryAsync(name, ct));
    public Task<TorrentTransferRecord?> GetTransferInfoAsync(CancellationToken ct = default) => UseAsync(() => client.GetTransferInfoAsync(ct));
    public Task<TorrentClientSnapshot?> GetMainDataAsync(long? revision = null, CancellationToken ct = default) => UseAsync(() => client.GetMainDataAsync(revision, ct));
    public Task<IReadOnlyDictionary<string, byte[]>> ExportResumeDataAsync(string hash, CancellationToken ct = default) => UseAsync(() => client.ExportResumeDataAsync(hash, ct));
}
