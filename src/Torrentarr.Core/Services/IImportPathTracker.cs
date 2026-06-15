namespace Torrentarr.Core.Services;

/// <summary>
/// Tracks content paths already sent to Arr scan commands (qBitrr <c>sent_to_scan</c> parity).
/// </summary>
public interface IImportPathTracker
{
    bool IsPathAlreadyScanned(string normalizedPath);
    bool IsHashAlreadyScanned(string hash);
    void MarkScanned(string normalizedPath, string hash);
    void RemoveEmptyPathsUnder(string completedFolderRoot);
    void ClearIfFolderEmpty(string completedFolderRoot);
}
