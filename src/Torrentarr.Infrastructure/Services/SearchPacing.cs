using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;

namespace Torrentarr.Infrastructure.Services;

internal sealed record SearchPacingObservation(int? ActiveDownloads, IReadOnlyDictionary<int, long>? Grabs, SearchLibraryGates? Library = null, SearchItemRest? Rest = null)
{
    internal string? DownloadHold(int ceiling) => ceiling <= 0 ? null : ActiveDownloads switch
    {
        null => "Active-download evidence unavailable",
        var active when active >= ceiling => $"Active-download ceiling reached ({active}/{ceiling})",
        _ => null
    };

    internal string? GrabHold(int itemId, int minutes, long now)
    {
        if (minutes <= 0) return null;
        if (Grabs == null || itemId <= 0) return "Arr grab history unavailable";
        var expiry = Grabs.GetValueOrDefault(itemId) + minutes * 60L;
        return expiry > now && Grabs.ContainsKey(itemId)
            ? $"Recent grab cooldown until {DateTimeOffset.FromUnixTimeSeconds(expiry):O}"
            : null;
    }

    internal string? Hold(int itemId, SearchConfig settings, long now) =>
        DownloadHold(settings.ActiveDownloadCeiling) ?? GrabHold(itemId, settings.RecentGrabCooldownMinutes, now);

    internal string? Hold(SonarrSearchPlan plan, ArrInstanceConfig arr, long now) =>
        plan.Covered.Select(item => Hold(item.ArrId, item.SeriesId ?? item.ArtistId ?? item.AuthorId,
                arr, now, plan.Command.Length == 0 ? item.Type : plan.Command))
            .FirstOrDefault(reason => reason != null);

    internal string? Hold(int itemId, int? ownerId, ArrInstanceConfig arr, long now, string scope = "") =>
        SearchQuietHours.NextEligible(arr.Search, DateTimeOffset.FromUnixTimeSeconds(now)) is { } next
            ? $"Quiet hours until {next:O}"
            : Hold(itemId, arr.Search, now) ?? (Library ?? new SearchLibraryGates(null)).Hold(itemId, ownerId, arr)
                ?? (Rest ?? new SearchItemRest(null)).Hold(itemId, scope, arr.Search.UnsuccessfulSearchRestMinutes, now);

}

internal static class SearchPacing
{
    internal static async Task<SearchPacingObservation> ObserveAsync(TorrentarrConfig config, ArrInstanceConfig arr,
        ITorrentClientRegistry? registry, TimeProvider clock, CancellationToken ct,
        Torrentarr.Infrastructure.Database.TorrentarrDbContext? db = null, string? instance = null)
    {
        var downloads = arr.Search.ActiveDownloadCeiling > 0
            ? ReadDownloadsAsync(config, arr, registry, ct) : Task.FromResult<int?>(null);
        var grabs = arr.Search.RecentGrabCooldownMinutes > 0
            ? ReadGrabsAsync(arr, clock, ct) : Task.FromResult<IReadOnlyDictionary<int, long>?>(null);
        var library = SearchLibraryGates.ObserveAsync(arr, ct);
        var rest = SearchItemRest.ObserveAsync(db, instance, arr, ct);
        await Task.WhenAll(downloads, grabs, library, rest);
        return new(await downloads, await grabs, await library, await rest);
    }

    private static async Task<int?> ReadDownloadsAsync(TorrentarrConfig config, ArrInstanceConfig arr,
        ITorrentClientRegistry? registry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arr.Category)) return null;
        var scoped = config.GetAllTorrentClients()
            .Where(pair => !pair.Value.Disabled && !string.IsNullOrWhiteSpace(pair.Value.Host) && pair.Value.Host != "CHANGE_ME"
                && CategoryPathHelper.CategoryEquals(arr.Category, CategoryOwnershipHelper.ResolveOwningCategory(config, arr.Category, pair.Key)))
            .Select(pair => (pair.Key, Client: registry?.GetClient(pair.Key))).ToArray();
        if (scoped.Length == 0 || scoped.Any(pair => pair.Client == null)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var counts = await Task.WhenAll(scoped.Select(async pair =>
            {
                var torrents = await pair.Client!.GetTorrentsAsync(cancellationToken: timeout.Token);
                return CountActive(config, arr, pair.Key, torrents);
            }));
            return counts.Any(count => count == null) ? null : counts.Sum();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    internal static int? CountActive(TorrentarrConfig config, ArrInstanceConfig arr, string clientId, IEnumerable<TorrentInfo> torrents)
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var torrent in torrents)
        {
            if (!CategoryPathHelper.CategoryEquals(arr.Category, CategoryOwnershipHelper.ResolveOwningCategory(config, torrent.Category, clientId)))
                continue;
            var state = torrent.State?.ToLowerInvariant();
            if (state is not ("downloading" or "forceddl" or "forceddownloading" or "metadl" or "forcedmetadl"
                or "metadatadownloading" or "stalleddl" or "stalleddownloading"))
            {
                if (state is "uploading" or "stalledup" or "forcedup" or "queuedup" or "pausedup" or "stoppedup"
                    or "queueddl" or "pauseddl" or "stoppeddl" or "checkingup" or "checkingdl" or "checkingresumedata"
                    or "moving" or "allocating" or "error" or "missingfiles") continue;
                return null;
            }
            if (string.IsNullOrWhiteSpace(torrent.Hash)) return null;
            active.Add(torrent.Hash);
        }
        return active.Count;
    }

    private static async Task<IReadOnlyDictionary<int, long>?> ReadGrabsAsync(ArrInstanceConfig arr, TimeProvider clock, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var since = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, clock.GetUtcNow().ToUnixTimeSeconds() - arr.Search.RecentGrabCooldownMinutes * 60L));
            var data = await ArrActivityCollector.ReadAsync(arr, $"history/since?date={Uri.EscapeDataString(since.ToString("O"))}&eventType=1", timeout.Token);
            return ReadGrabDates(arr.Type, data, clock.GetUtcNow().ToUnixTimeSeconds());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    internal static IReadOnlyDictionary<int, long>? ReadGrabDates(string type, JArray history, long now)
    {
        var field = type.ToLowerInvariant() switch
        {
            "radarr" => "movieId",
            "sonarr" => "episodeId",
            "lidarr" => "albumId",
            "readarr" => "bookId",
            _ => null
        };
        if (field == null) return null;
        var dates = new Dictionary<int, long>();
        foreach (var token in history)
        {
            if (token is not JObject entry) return null;
            if (entry["eventType"]?.Value<string>()?.ToLowerInvariant() is not ("grabbed" or "1")) continue;
            var at = ArrActivityCollector.EvidenceTimestamp(entry["date"]);
            if (!int.TryParse(entry[field]?.Value<string>(), out var id) || id <= 0 || at < 0 || at > now) return null;
            dates[id] = Math.Max(dates.GetValueOrDefault(id), at);
        }
        return dates;
    }
}
