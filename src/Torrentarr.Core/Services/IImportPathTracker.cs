namespace Torrentarr.Core.Services;

/// <summary>
/// Tracks content paths already sent to Arr scan commands (qBitrr <c>sent_to_scan</c> parity).
/// </summary>
public interface IImportPathTracker
{
    bool IsPathAlreadyScanned(string instanceId, string normalizedPath);
    bool IsHashAlreadyScanned(string instanceId, string hash);
    void Clear();
    void MarkScanned(string instanceId, string normalizedPath, string hash);
    void RemoveEmptyPathsUnder(string completedFolderRoot);
    void ClearIfFolderEmpty(string completedFolderRoot);
}
