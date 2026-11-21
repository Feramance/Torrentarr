using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

internal sealed record FailedTorrentReceipt(long AddedOn, string Server, string ClientHost, string OwnerCategory, string Source,
    string Destination, string SavePath, string ContentPath, string Stage, int[] QueueIds,
    bool Blocklist, bool Replace, SearchCandidate[] Candidates, int[]? AttemptedQueueIds = null)
{
    internal static string Key(TorrentInfo torrent) =>
        MaintenancePersistence.Key(torrent.ClientInstanceId, torrent.Hash, "failed-parking");

    internal static FailedTorrentReceipt Parse(string raw)
    {
        var value = JsonConvert.DeserializeObject<FailedTorrentReceipt>(raw);
        if (value == null || value.AddedOn <= 0 || string.IsNullOrEmpty(value.Server) || string.IsNullOrEmpty(value.ClientHost)
            || string.IsNullOrEmpty(value.OwnerCategory) || string.IsNullOrEmpty(value.Source) || string.IsNullOrEmpty(value.Destination)
            || string.IsNullOrEmpty(value.SavePath) || string.IsNullOrEmpty(value.ContentPath)
            || value.Stage is not ("moving" or "queue" or "done") || value.QueueIds == null || value.Candidates == null)
            throw new InvalidDataException("Failed torrent routing receipt is incomplete.");
        return value;
    }

    internal static bool BlocksRemoval(TorrentarrDbContext db, TorrentInfo torrent)
    {
        var key = Key(torrent);
        var raw = db.MaintenanceState.AsNoTracking().Where(row => row.Key == key).Select(row => row.Data).SingleOrDefault();
        if (raw == null) return false;
        try
        {
            var receipt = Parse(raw);
            return receipt.Stage != "done" || torrent.AddedOn <= 0 || receipt.AddedOn == torrent.AddedOn;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return true; }
    }
}

public partial class ArrImportService
{
    private sealed record ParkingQueueRow(int Id, string? Hash, SearchCandidate[] Candidates);
    private sealed record ParkingQueue(IArrActivityClient Adapter, Func<CancellationToken, Task<ParkingQueueRow[]>> Read,
        Func<int, bool, CancellationToken, Task<bool>> Remove) : IDisposable
    {
        public void Dispose() => Adapter.Dispose();
    }

    public async Task ParkFailedTorrentAsync(TorrentInfo expected, string category, bool blocklist, bool replace,
        CancellationToken ct = default)
    {
        using var snapshot = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        var instance = CategoryOwnershipHelper.FindArrSectionForCategory(_config, category);
        if (instance == null || !_config.ArrInstances.TryGetValue(instance, out var arr) || !FailedTorrentRouting.Enabled(arr)) return;
        var issue = ParkingIssue(instance, expected.ClientInstanceId, expected.Hash);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        try
        {
            var settings = _config.GetTorrentClient(expected.ClientInstanceId);
            var client = _qbitManager.GetClient(expected.ClientInstanceId);
            var destination = arr.Torrent.FailedTorrentDestinations.SingleOrDefault(row =>
                row.Client.Equals(expected.ClientInstanceId, StringComparison.OrdinalIgnoreCase))?.Category;
            if (!_dbContext.Database.IsSqlite() || settings == null || !FailedTorrentRouting.InScope(settings)
                || client == null || !client.Capabilities.Categories || !client.Capabilities.AutomaticManagement)
                throw new IOException("A supported, connected qBittorrent client and durable database are required.");
            if (string.IsNullOrWhiteSpace(destination)) throw new IOException("No destination is configured for this client.");
            if (FailedTorrentRouting.DestinationError(_config, arr, expected.ClientInstanceId, destination) is { } invalid)
                throw new IOException(invalid);
            if (ImportRescueReceipt.BlocksRemoval(_dbContext, expected, TorrentRemovalIntent.RejectedByAllowlist))
                throw new IOException("An import rescue already owns this download.");

            using var queue = OpenParkingQueue(arr);
            var current = await ReadParkingTorrentAsync(expected.ClientInstanceId, expected.Hash, token);
            if (!SameParkingPayload(expected, current) || !SeedingService.SameRemovalCandidate(expected, current!)
                || !CategoryOwnershipHelper.ArrOwnsTorrent(_config, arr, current!))
                throw new IOException("The source torrent, its ownership, or its payload changed.");
            var categories = await client.GetCategoriesAsync(token);
            if (!categories.Keys.Contains(destination, StringComparer.Ordinal))
                throw new IOException("The configured destination does not exist on this client.");
            var key = FailedTorrentReceipt.Key(expected);
            var previous = await ReadParkingReceiptAsync(key, token);
            var receipt = previous == null ? null : FailedTorrentReceipt.Parse(previous);
            var server = ArrActivityTracking.ServerKey(arr);
            var ownerCategory = CategoryPathHelper.NormalizeCategory(arr.Category).ToUpperInvariant();
            var clientHost = ParkingClientIdentity(settings);
            if (receipt != null && receipt.AddedOn == expected.AddedOn && receipt.Stage == "done") return;
            if (receipt != null && receipt.Stage != "done" && (receipt.AddedOn != expected.AddedOn
                || receipt.Server != server || receipt.OwnerCategory != ownerCategory || receipt.ClientHost != clientHost || receipt.Source != expected.Category
                || receipt.Destination != destination || receipt.SavePath != current!.SavePath
                || receipt.ContentPath != current.ContentPath))
                throw new IOException("An earlier move has unresolved or changed evidence.");
            if (receipt == null || receipt.Stage == "done")
            {
                var matches = (await queue.Read(token)).Where(row => HashMatches(row.Hash, expected.Hash)).ToArray();
                if (matches.Length == 0 || matches.Any(row => row.Id <= 0))
                    throw new IOException("A matching Arr queue entry could not be verified.");
                if (replace && matches.Any(row => row.Candidates.Length == 0))
                    throw new IOException("Replacement requires verified Arr media identity.");
                receipt = new(expected.AddedOn, server, clientHost, ownerCategory, expected.Category, destination, current!.SavePath, current.ContentPath,
                    "moving", matches.Select(row => row.Id).Distinct().ToArray(), blocklist, replace,
                    matches.SelectMany(row => row.Candidates).DistinctBy(candidate => candidate.ArrId).ToArray());
                await using var claim = await _dbContext.Database.BeginTransactionAsync(token);
                if (ImportRescueReceipt.BlocksRemoval(_dbContext, expected, TorrentRemovalIntent.RejectedByAllowlist)
                    || !await StoreParkingAsync(key, receipt, previous, token)) return;
                await claim.CommitAsync(token);
            }
            if (receipt.Stage != "moving") throw new IOException("The category no longer agrees with the saved move.");
            if (_config.Revision != _config.LatestRevision) return;
            // Re-read after recording the intent. A manually managed torrent retains its storage path on category change.
            current = await ReadParkingTorrentAsync(expected.ClientInstanceId, expected.Hash, token);
            if (!SameParkingPayload(expected, current) || current!.Category != receipt.Source
                || !SeedingService.SameRemovalCandidate(expected, current))
                throw new IOException("The torrent changed before the category update.");
            if (!await client.SetCategoryAsync([expected.Hash], destination, token))
                throw new IOException("The client rejected the category update.");
            await FinishParkingAsync(instance, arr, expected.ClientInstanceId, expected.Hash, receipt, queue, token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (_issues != null) await _issues.ReportAsync(issue.WithEvidence(DiagnosticText.Error(ex)), ct);
            _logger.LogWarning("Failed torrent category operation held for {Client}/{Hash}: {Reason}",
                expected.ClientInstanceId, expected.Hash, DiagnosticText.Error(ex));
        }
    }

    public async Task ReconcileFailedTorrentMovesAsync(string category, CancellationToken ct = default)
    {
        if (!_dbContext.Database.IsSqlite()) return;
        using var snapshot = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        var instance = CategoryOwnershipHelper.FindArrSectionForCategory(_config, category);
        if (instance == null || !_config.ArrInstances.TryGetValue(instance, out var arr)) return;
        var server = ArrActivityTracking.ServerKey(arr);
        var ownerCategory = CategoryPathHelper.NormalizeCategory(arr.Category).ToUpperInvariant();
        var cursorKey = MaintenancePersistence.Key("", "", $"cursor:failed-parking:{server}:{ownerCategory}");
        var cursor = MaintenancePersistence.Read<ScanCursor>(_dbContext, cursorKey);
        var rows = await _dbContext.MaintenanceState.FromSqlInterpolated($"""
            SELECT Key, Data FROM torrentarr_maintenance_state
            WHERE substr(Key, -15) = '|failed-parking'
                AND CASE WHEN json_valid(Data) THEN json_extract(Data, '$.Server') END = {server}
                AND CASE WHEN json_valid(Data) THEN json_extract(Data, '$.OwnerCategory') END = {ownerCategory}
                AND CASE WHEN json_valid(Data) THEN json_extract(Data, '$.Stage') END IN ('moving', 'queue')
            ORDER BY CASE WHEN Key > {cursor.Position} THEN 0 ELSE 1 END, Key
            LIMIT 50
            """).AsNoTracking().ToListAsync(ct);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        foreach (var row in rows)
        {
            if (budget.IsCancellationRequested || _config.Revision != _config.LatestRevision) break;
            cursor.Position = row.Key;
            var separator = row.Key.IndexOf('|');
            if (separator <= 0 || row.Key.Length <= separator + 16) continue;
            var clientId = _config.GetAllTorrentClients().Keys.FirstOrDefault(id =>
                id.Equals(row.Key[..separator], StringComparison.OrdinalIgnoreCase)) ?? row.Key[..separator];
            var hash = row.Key[(separator + 1)..^15];
            try
            {
                var receipt = FailedTorrentReceipt.Parse(row.Data);
                var current = await ReadParkingTorrentAsync(clientId, hash, budget.Token);
                if (receipt.Stage == "moving" && current?.Category == receipt.Source) continue;
                using var queue = OpenParkingQueue(arr);
                await FinishParkingAsync(instance, arr, clientId, hash, receipt, queue, budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_issues != null)
                    await _issues.ReportAsync(ParkingIssue(instance, clientId, hash).WithEvidence(DiagnosticText.Error(ex)), ct);
            }
        }
        ct.ThrowIfCancellationRequested();
        if (rows.Count > 0 && _config.Revision == _config.LatestRevision)
            MaintenancePersistence.Write(_dbContext, cursorKey, cursor);
    }

    private async Task FinishParkingAsync(string instance, ArrInstanceConfig arr, string clientId, string hash,
        FailedTorrentReceipt receipt, ParkingQueue queue, CancellationToken ct)
    {
        var expected = new TorrentInfo
        {
            Hash = hash,
            ClientInstanceId = clientId,
            AddedOn = receipt.AddedOn,
            SavePath = receipt.SavePath,
            ContentPath = receipt.ContentPath
        };
        var current = await ReadParkingTorrentAsync(clientId, hash, ct);
        var settings = _config.GetTorrentClient(clientId);
        if (!SameParkingPayload(expected, current) || current!.Category != receipt.Destination
            || settings == null || ParkingClientIdentity(settings) != receipt.ClientHost
            || FailedTorrentRouting.DestinationError(_config, arr, clientId, receipt.Destination) != null
            || ImportRescueReceipt.BlocksRemoval(_dbContext, expected, TorrentRemovalIntent.RejectedByAllowlist))
            throw new IOException("The moved torrent's identity, ownership or retained payload could not be verified.");
        var key = FailedTorrentReceipt.Key(expected);
        var raw = await ReadParkingReceiptAsync(key, ct) ?? throw new IOException("The move receipt disappeared.");
        var saved = FailedTorrentReceipt.Parse(raw);
        if (saved.Stage == "done") return;
        if (saved.AddedOn != receipt.AddedOn || saved.Destination != receipt.Destination || saved.Server != receipt.Server
            || saved.OwnerCategory != CategoryPathHelper.NormalizeCategory(arr.Category).ToUpperInvariant())
            throw new IOException("The move receipt changed.");
        receipt = saved;
        if (receipt.Stage == "moving")
        {
            receipt = receipt with { Stage = "queue" };
            if (!await StoreParkingAsync(key, receipt, raw, ct)) return;
            raw = JsonConvert.SerializeObject(receipt);
        }
        while (true)
        {
            var matches = (await queue.Read(ct)).Where(row => HashMatches(row.Hash, hash)).ToArray();
            if (matches.Any(row => !receipt.QueueIds.Contains(row.Id)))
                throw new IOException("Arr reported a new queue identity after the move.");
            if (matches.Length == 0)
            {
                if (receipt.Blocklist && receipt.AttemptedQueueIds is not { Length: > 0 })
                    throw new IOException("The Arr queue cleared before blocklisting was possible. Review the retained torrent in Arr.");
                break;
            }
            if (receipt.AttemptedQueueIds is { Length: > 0 } attempted && matches.Any(row => attempted.Contains(row.Id)))
                throw new IOException("An earlier blocklist response is uncertain. Verify the Arr queue and blocklist before retrying.");
            var rowId = matches[0].Id;
            if (_config.Revision != _config.LatestRevision) return;
            if (receipt.Blocklist)
            {
                // Reserve this side effect before sending it so ambiguous responses cannot double-blocklist.
                receipt = receipt with { AttemptedQueueIds = [.. receipt.AttemptedQueueIds ?? [], rowId] };
                if (!await StoreParkingAsync(key, receipt, raw, ct)) return;
                raw = JsonConvert.SerializeObject(receipt);
            }
            try
            {
                if (!await queue.Remove(rowId, receipt.Blocklist, ct))
                    throw new IOException("Arr did not confirm the queue-only removal.");
            }
            catch (ArrApiException ex) when (receipt.Blocklist && ex.StatusCode is
                System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                await StoreParkingAsync(key, receipt with
                { AttemptedQueueIds = receipt.AttemptedQueueIds!.Where(id => id != rowId).ToArray() }, raw, ct);
                throw;
            }
            if ((await queue.Read(ct)).Any(row => HashMatches(row.Hash, hash) && row.Id == rowId))
                throw new IOException("Arr still reports the queue row after removal.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        if (!await StoreParkingAsync(key, receipt with { Stage = "done" }, raw, ct)) return;
        if (receipt.Replace)
        {
            var candidates = System.Text.Json.JsonSerializer.Serialize(receipt.Candidates);
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO torrentarr_sonarr_search_retries (ArrInstance, DownloadId, CandidatesJson, BlocklistPending, ConsecutiveFailures)
                VALUES ({instance}, {hash.ToUpperInvariant()}, {candidates}, 0, 0)
                ON CONFLICT(ArrInstance, DownloadId) DO NOTHING
                """, ct);
        }
        await transaction.CommitAsync(ct);
        if (_issues != null) await _issues.ResolveAsync(ParkingIssue(instance, clientId, hash), ct);
        _logger.LogInformation("Retained failed torrent {Client}/{Hash} in {Category} after confirming the Arr queue outcome",
            clientId, hash, DiagnosticText.Safe(receipt.Destination));
    }

    private static string ParkingClientIdentity(TorrentClientInstanceConfig settings) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new { settings.Host, settings.Port, settings.Type })));

    private static OperationalIssueReport ParkingIssue(string instance, string client, string hash) => new(
        "torrent.failed-category", "automation", "Failed torrent category move needs attention",
        "The retained torrent and its Arr queue outcome are held for verification.",
        "Check the client category, automatic torrent management, save path and the Arr queue.",
        ArrInstance: instance, ClientInstance: client, SubjectType: "torrent", SubjectId: hash.ToUpperInvariant());

    private static bool HashMatches(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameParkingPayload(TorrentInfo expected, TorrentInfo? current) =>
        current != null && expected.AddedOn > 0 && current.AddedOn == expected.AddedOn && current.AutoManaged == false
        && current.State?.ToLowerInvariant() is not (null or "" or "unknown" or "moving" or "checkingup" or "checkingdl" or "checkingresumedata")
        && IsAbsoluteParkingPath(expected.SavePath) && IsAbsoluteParkingPath(expected.ContentPath)
        && string.Equals(expected.SavePath, current.SavePath, StringComparison.Ordinal)
        && string.Equals(expected.ContentPath, current.ContentPath, StringComparison.Ordinal);

    private static bool IsAbsoluteParkingPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Any(char.IsControl)
        && (path.StartsWith('/') || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\');

    private async Task<TorrentInfo?> ReadParkingTorrentAsync(string clientId, string hash, CancellationToken ct)
    {
        TorrentInfo? match = null;
        foreach (var (id, settings) in _config.GetAllTorrentClients())
        {
            if (!FailedTorrentRouting.InScope(settings)) continue;
            var client = _qbitManager.GetClient(id) ?? throw new IOException("A configured qBittorrent client is unavailable.");
            foreach (var torrent in await client.GetTorrentsAsync(cancellationToken: ct))
            {
                if (!HashMatches(torrent.Hash, hash)) continue;
                if (match != null || !id.Equals(clientId, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Torrent identity is ambiguous across clients.");
                torrent.ClientInstanceId = id;
                match = torrent;
            }
        }
        return match;
    }

    private Task<string?> ReadParkingReceiptAsync(string key, CancellationToken ct) =>
        _dbContext.MaintenanceState.AsNoTracking().Where(row => row.Key == key).Select(row => row.Data).SingleOrDefaultAsync(ct);

    private async Task<bool> StoreParkingAsync(string key, FailedTorrentReceipt receipt, string? previous, CancellationToken ct)
    {
        if (_config.Revision != _config.LatestRevision) return false;
        var data = JsonConvert.SerializeObject(receipt);
        return await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO torrentarr_maintenance_state (Key, Data) VALUES ({key}, {data})
            ON CONFLICT(Key) DO UPDATE SET Data=excluded.Data WHERE Data={previous ?? ""}
            """, ct) == 1;
    }

    private static SearchCandidate[] ParkingCandidates(string type, int? id, int? series = null) =>
        id is > 0 ? [new() { ArrId = id.Value, Type = type, SeriesId = series, Reason = "Retry" }] : [];

    private static ParkingQueue OpenParkingQueue(ArrInstanceConfig arr)
    {
        switch (arr.Type.ToLowerInvariant())
        {
            case "radarr":
                var r = new RadarrClient(arr.URI, arr.APIKey, arr.SkipTLSVerify);
                return new(r, async ct => (await r.GetAllQueueAsync(ct)).Select(row =>
                    new ParkingQueueRow(row.Id, row.DownloadId, ParkingCandidates("Movie", row.MovieId))).ToArray(),
                    (id, block, ct) => r.DeleteFromQueueAsync(id, false, block, ct, skipRedownload: true, throwOnFailure: true, singleAttempt: true));
            case "sonarr":
                var s = new SonarrClient(arr.URI, arr.APIKey, arr.SkipTLSVerify);
                return new(s, async ct => (await s.GetAllQueueAsync(ct)).Select(row =>
                    new ParkingQueueRow(row.Id, row.DownloadId, row.EpisodeIds.SelectMany(id =>
                        ParkingCandidates("Episode", id, row.SeriesId)).ToArray())).ToArray(),
                    (id, block, ct) => s.DeleteFromQueueAsync(id, false, block, ct, skipRedownload: true, throwOnFailure: true, singleAttempt: true));
            case "lidarr":
                var l = new LidarrClient(arr.URI, arr.APIKey, arr.SkipTLSVerify);
                return new(l, async ct => (await l.GetAllQueueAsync(ct)).Select(row =>
                    new ParkingQueueRow(row.Id, row.DownloadId, ParkingCandidates("Album", row.AlbumId))).ToArray(),
                    (id, block, ct) => l.DeleteFromQueueAsync(id, false, block, ct, skipRedownload: true, throwOnFailure: true, singleAttempt: true));
            case "readarr":
                var b = new ReadarrClient(arr.URI, arr.APIKey, arr.SkipTLSVerify);
                return new(b, async ct => (await b.GetAllQueueAsync(ct)).Select(row =>
                    new ParkingQueueRow(row.Id, row.DownloadId, ParkingCandidates("Book", row.BookId))).ToArray(),
                    (id, block, ct) => b.DeleteFromQueueAsync(id, false, block, ct, skipRedownload: true, throwOnFailure: true, singleAttempt: true));
            default: throw new IOException("This Arr adapter has no verified queue-only removal.");
        }
    }
}
