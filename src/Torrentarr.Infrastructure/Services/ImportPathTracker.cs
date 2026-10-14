using System.Collections.Concurrent;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public class ImportPathTracker : IImportPathTracker
{
    private readonly ConcurrentDictionary<(string Instance, string Path), string> _scanned = new();
    private readonly HashSet<(string Instance, string Hash)> _hashes = [];
    private readonly object _gate = new();
    private static string Instance(string id) => id.ToUpperInvariant();
    private static string PathKey(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    public bool IsPathAlreadyScanned(string instanceId, string normalizedPath)
    {
        lock (_gate) return !string.IsNullOrEmpty(normalizedPath) && _scanned.ContainsKey((Instance(instanceId), PathKey(normalizedPath)));
    }

    public void Clear()
    {
        lock (_gate) { _scanned.Clear(); _hashes.Clear(); }
    }

    public bool IsHashAlreadyScanned(string instanceId, string hash)
    {
        lock (_gate) return !string.IsNullOrEmpty(hash) && _hashes.Contains((Instance(instanceId), hash.ToUpperInvariant()));
    }

    public void MarkScanned(string instanceId, string normalizedPath, string hash)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(normalizedPath))
                _scanned[(Instance(instanceId), PathKey(normalizedPath))] = hash;
            if (!string.IsNullOrEmpty(hash)) _hashes.Add((Instance(instanceId), hash.ToUpperInvariant()));
        }
    }

    public void RemoveEmptyPathsUnder(string completedFolderRoot)
    {
        if (string.IsNullOrWhiteSpace(completedFolderRoot) || !Directory.Exists(completedFolderRoot)) return;
        foreach (var path in Directory.EnumerateDirectories(completedFolderRoot, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
        {
            try
            {
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Prune(completedFolderRoot);
    }

    public void ClearIfFolderEmpty(string completedFolderRoot)
    {
        if (!string.IsNullOrWhiteSpace(completedFolderRoot)) Prune(completedFolderRoot);
    }

    private void Prune(string root)
    {
        var prefix = PathKey(Path.TrimEndingDirectorySeparator(root)) + Path.DirectorySeparatorChar;
        lock (_gate)
        {
            foreach (var entry in _scanned)
                if ((entry.Key.Path == PathKey(root) || entry.Key.Path.StartsWith(prefix, StringComparison.Ordinal))
                    && !File.Exists(entry.Key.Path) && !Directory.Exists(entry.Key.Path))
                {
                    _scanned.TryRemove(entry.Key, out _);
                    if (!_scanned.Any(p => p.Key.Instance == entry.Key.Instance && p.Value.Equals(entry.Value, StringComparison.OrdinalIgnoreCase)))
                        _hashes.Remove((entry.Key.Instance, entry.Value.ToUpperInvariant()));
                }
        }
    }
}
