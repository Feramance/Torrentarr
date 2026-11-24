using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.Endpoints;

public static class SharedFileFilterEndpoints
{
    public static void MapSharedFileFilters(this WebApplication app, string prefix)
    {
        app.MapGet(prefix + "/file-filters/preview", (string instance, string? path,
            TorrentarrConfig config, SharedFileFilterService lists) =>
        {
            using var operation = config.BeginOperation();
            if (!config.ArrInstances.TryGetValue(instance, out var arr)) return Results.NotFound();
            if (path is { Length: > SharedFileFilterSource.MaxPathLength }) return Results.BadRequest();
            var view = lists.Capture(arr.Torrent);
            var available = view.Available;
            string? reason = null;
            if (available && !string.IsNullOrEmpty(path))
            {
                try
                {
                    var options = arr.Torrent.CaseSensitiveMatches ? RegexOptions.None : RegexOptions.IgnoreCase;
                    reason = FileFilterMatcher.LocalReason(path, arr.Torrent, options) ?? view.Match(path);
                }
                catch (RegexMatchTimeoutException) { return Results.BadRequest(new { error = "Pattern evaluation exceeded its time limit." }); }
            }

            return Results.Ok(new
            {
                available,
                reason,
                lists = arr.Torrent.SharedFileFilters.Select(name =>
                {
                    var state = lists.Status.SingleOrDefault(item => item.Name == name);
                    var current = config.Settings.SharedFileFilters.FirstOrDefault(item => item.Name == name);
                    return new
                    {
                        name,
                        source = current == null ? null : DiagnosticText.Safe(current.Source),
                        loadedAt = state?.Source == current?.Source ? state?.LoadedAt : null,
                        checkedAt = state?.CheckedAt,
                        error = state?.Error ?? (available ? null : "List has no usable version for the configured source.")
                    };
                })
            });
        });
    }
}
