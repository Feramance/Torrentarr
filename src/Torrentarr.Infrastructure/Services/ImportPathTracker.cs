using System.Collections.Concurrent;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// In-memory <c>sent_to_scan</c> / <c>sent_to_scan_hashes</c> tracking (qBitrr arss.py parity).
/// </summary>
public class ImportPathTracker : IImportPathTracker
{
    private readonly ConcurrentDictionary<string, byte> _scannedPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _scannedHashes =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsPathAlreadyScanned(string normalizedPath) =>
        !string.IsNullOrEmpty(normalizedPath) && _scannedPaths.ContainsKey(normalizedPath);

    public bool IsHashAlreadyScanned(string hash) =>
        !string.IsNullOrEmpty(hash) && _scannedHashes.ContainsKey(hash.ToUpperInvariant());

    public void MarkScanned(string normalizedPath, string hash)
    {
        if (!string.IsNullOrEmpty(normalizedPath))
            _scannedPaths[normalizedPath] = 0;
        if (!string.IsNullOrEmpty(hash))
            _scannedHashes[hash.ToUpperInvariant()] = 0;
    }

    public void RemoveEmptyPathsUnder(string completedFolderRoot)
    {
        if (string.IsNullOrWhiteSpace(completedFolderRoot) || !Directory.Exists(completedFolderRoot))
            return;

        var newSent = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateDirectories(completedFolderRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(p => p.Length))
        {
            try
            {
                if (!Directory.Exists(path))
                    continue;
                if (Directory.EnumerateFileSystemEntries(path).Any())
                    continue;
                Directory.Delete(path);
                if (_scannedPaths.ContainsKey(path))
                    _scannedPaths.TryRemove(path, out _);
            }
            catch
            {
                // best-effort
            }
        }

        foreach (var p in _scannedPaths.Keys)
        {
            if (Directory.Exists(p))
                newSent[p] = 0;
        }
        _scannedPaths.Clear();
        foreach (var kv in newSent)
            _scannedPaths[kv.Key] = kv.Value;
    }

    public void ClearIfFolderEmpty(string completedFolderRoot)
    {
        if (string.IsNullOrWhiteSpace(completedFolderRoot) || !Directory.Exists(completedFolderRoot))
            return;

        try
        {
            if (!Directory.EnumerateFileSystemEntries(completedFolderRoot).Any())
            {
                _scannedPaths.Clear();
                _scannedHashes.Clear();
            }
        }
        catch
        {
            // best-effort
        }
    }
}
