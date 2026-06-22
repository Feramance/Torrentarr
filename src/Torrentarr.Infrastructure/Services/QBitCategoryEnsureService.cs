using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Ensures Arr/qBit categories exist on all instances (qBitrr <c>_ensure_category_on_all_instances</c>).
/// </summary>
public class QBitCategoryEnsureService
{
    private readonly ILogger<QBitCategoryEnsureService> _logger;
    private readonly TorrentarrConfig _config;
    private readonly QBittorrentConnectionManager _qbitManager;

    public QBitCategoryEnsureService(
        ILogger<QBitCategoryEnsureService> logger,
        TorrentarrConfig config,
        QBittorrentConnectionManager qbitManager)
    {
        _logger = logger;
        _config = config;
        _qbitManager = qbitManager;
    }

    public async Task EnsureCategoryOnAllInstancesAsync(string category, CancellationToken ct = default)
    {
        var leaf = CategoryPathHelper.NormalizeCategory(category);
        if (string.IsNullOrEmpty(leaf))
            return;

        var prefixPaths = CategoryPathHelper.CategoryParents(leaf);
        if (prefixPaths.Count == 0)
            prefixPaths = new[] { leaf }.ToList();
        else if (!prefixPaths.Contains(leaf, StringComparer.Ordinal))
            prefixPaths = prefixPaths.Append(leaf).ToList();

        var completedRoot = ResolveCompletedRoot();

        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            try
            {
                var categories = await client.GetCategoriesAsync(ct);
                foreach (var parent in prefixPaths)
                {
                    if (categories.ContainsKey(parent))
                        continue;

                    var parentsOfParent = CategoryPathHelper.CategoryParents(parent);
                    string savePath;
                    if (parentsOfParent.Count > 0
                        && categories.TryGetValue(parentsOfParent[^1], out var parentInfo)
                        && !string.IsNullOrEmpty(parentInfo.SavePath))
                    {
                        savePath = Path.Combine(parentInfo.SavePath, parent.Split('/').Last());
                    }
                    else
                    {
                        savePath = Path.Combine(completedRoot, parent.Replace('/', Path.DirectorySeparatorChar));
                    }

                    var created = await client.CreateCategoryAsync(parent, savePath, ct);
                    if (created)
                    {
                        _logger.LogInformation(
                            "Created category '{Category}' on instance '{Instance}' (save_path={Path})",
                            parent, instanceName, savePath);
                        categories[parent] = new CategoryInfo { Name = parent, SavePath = savePath };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed ensuring category '{Category}' on instance '{Instance}'", leaf, instanceName);
            }
        }
    }

    private string ResolveCompletedRoot()
    {
        var folder = _config.Settings.CompletedDownloadFolder;
        if (!string.IsNullOrWhiteSpace(folder) && folder != "CHANGE_ME" && Directory.Exists(folder))
            return folder;

        foreach (var (_, qbit) in _config.QBitInstances)
        {
            if (!string.IsNullOrWhiteSpace(qbit.DownloadPath) && qbit.DownloadPath != "CHANGE_ME"
                && Directory.Exists(qbit.DownloadPath))
                return qbit.DownloadPath;
        }

        return folder is { Length: > 0 } ? folder : "/downloads";
    }
}
