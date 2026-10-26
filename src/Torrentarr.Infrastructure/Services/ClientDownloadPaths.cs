using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

public static class ClientDownloadPaths
{
    public static void Clean(TorrentarrConfig config, IImportPathTracker tracker)
    {
        var mapper = new PathMappingService();
        var roots = config.GetAllTorrentClients().Values.Where(c => !c.Disabled && !string.IsNullOrWhiteSpace(c.DownloadPath))
            .Select(c => c.Maintenance.PathMappings.Count > 0
                ? mapper.MapToLocal(c.DownloadPath!, c.Maintenance.PathMappings)
                : Path.IsPathFullyQualified(c.DownloadPath!) ? c.DownloadPath : null)
            .Where(p => p != null).Select(p => Path.GetFullPath(p!))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var root in roots)
        {
            // Existing cleanup recursively deletes empty directories; never expand it to a filesystem root.
            if (Path.TrimEndingDirectorySeparator(root) == Path.GetPathRoot(root) || mapper.ValidateLocalPath(root, root) != null) continue;
            tracker.RemoveEmptyPathsUnder(root);
            tracker.ClearIfFolderEmpty(root);
        }
    }
}
