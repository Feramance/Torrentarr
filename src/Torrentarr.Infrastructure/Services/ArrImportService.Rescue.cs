using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Services;

internal sealed class ImportRescueReceipt
{
    public long AddedOn { get; set; }
    public string Server { get; set; } = "";
    public int MovieId { get; set; }
    public string Path { get; set; } = "";
    public string ContentPath { get; set; } = "";
    public string SavePath { get; set; } = "";
    public long SubmittedAt { get; set; }
    public string Stage { get; set; } = "";
    internal static string Key(TorrentInfo torrent) => MaintenancePersistence.Key(torrent.ClientInstanceId, torrent.Hash, "import-rescue");
    internal static ImportRescueReceipt Parse(string data)
    {
        var receipt = JsonConvert.DeserializeObject<ImportRescueReceipt>(data);
        if (receipt == null || receipt.Stage is not ("pending" or "confirmed" or "unconfirmed" or "clear")
            || receipt.AddedOn <= 0 || receipt.MovieId <= 0 || receipt.SubmittedAt is <= 0 or > 253402300799
            || string.IsNullOrWhiteSpace(receipt.Server) || receipt.Server.Length != 64 || !receipt.Server.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(receipt.Path) || string.IsNullOrWhiteSpace(receipt.ContentPath)
            || string.IsNullOrWhiteSpace(receipt.SavePath))
            throw new InvalidDataException("Stored import rescue evidence is incomplete; preserving its claim.");
        return receipt;
    }

    internal static bool BlocksRemoval(TorrentarrDbContext db, TorrentInfo torrent, TorrentRemovalIntent intent)
    {
        var key = Key(torrent);
        var data = db.MaintenanceState.AsNoTracking().Where(row => row.Key == key).Select(row => row.Data).FirstOrDefault();
        if (data == null) return false;
        ImportRescueReceipt receipt;
        try { receipt = Parse(data); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return true; }
        var failed = intent is TorrentRemovalIntent.StalledDownload or TorrentRemovalIntent.RejectedByAllowlist or TorrentRemovalIntent.RejectedByCustomFormat;
        return receipt.Stage == "pending" || receipt.AddedOn == torrent.AddedOn && receipt.Stage == "confirmed" && failed;
    }
}

public partial class ArrImportService
{
    internal const string RadarrIdMatchWarning = "Found matching movie via grab history, but release was matched to movie by ID. Manual Import required.";
    private static bool SupportedRescue(QueueItem item) => item.Id > 0 && item.MovieId > 0 && item.SizeLeft == 0
        && item.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
        && string.Equals(item.TrackedDownloadStatus, "warning", StringComparison.OrdinalIgnoreCase)
        && string.Equals(item.TrackedDownloadState, "importBlocked", StringComparison.OrdinalIgnoreCase)
        && item.StatusMessages?.Any(message => message.Title == RadarrIdMatchWarning
            || message.Messages?.Contains(RadarrIdMatchWarning) == true) == true;

    public Task<ImportRescueOutcome> TryRescueImportAsync(TorrentInfo torrent, string category, CancellationToken ct = default)
        => TryRescueImportCoreAsync(torrent, category, allowSubmission: true, allowMissing: false, ct);

    public async Task ReconcileImportRescuesAsync(string category, CancellationToken ct = default)
    {
        if (!_dbContext.Database.IsSqlite()) return;
        using var configuration = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        var instance = CategoryOwnershipHelper.FindArrSectionForCategory(_config, category);
        if (instance == null || !_config.ArrInstances.TryGetValue(instance, out var arr)
            || !arr.Type.Equals("radarr", StringComparison.OrdinalIgnoreCase)) return;
        var server = ArrActivityTracking.ServerKey(arr);
        var cursorKey = MaintenancePersistence.Key("", "", "cursor:import-rescue:" + server);
        var cursor = MaintenancePersistence.Read<ScanCursor>(_dbContext, cursorKey);
        var rows = await _dbContext.MaintenanceState.FromSqlInterpolated($"""
            SELECT Key, Data FROM torrentarr_maintenance_state
            WHERE substr(Key, -14) = '|import-rescue'
                AND CASE WHEN json_valid(Data) THEN json_extract(Data, '$.Stage') END = 'pending'
                AND CASE WHEN json_valid(Data) THEN json_extract(Data, '$.Server') END = {server}
            ORDER BY CASE WHEN Key > {cursor.Position} THEN 0 ELSE 1 END, Key
            LIMIT 100
            """).AsNoTracking().ToListAsync(ct);
        if (rows.Count == 0) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        var inventories = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (_config.Revision != _config.LatestRevision) break;
            cursor.Position = row.Key;
            var issue = new OperationalIssueReport("import.rescue-reconciliation", "automation", "Import rescue reconciliation is blocked",
                "Stored import rescue evidence could not be reconciled. Its claim is retained.",
                "Check client connectivity, Arr history and the saved rescue receipt.", ArrInstance: instance,
                SubjectId: row.Key);
            try
            {
                var clientName = _config.GetAllTorrentClients().Keys.FirstOrDefault(name =>
                    row.Key.StartsWith(name.ToUpperInvariant() + "|", StringComparison.Ordinal));
                if (clientName == null) throw new InvalidOperationException("Receipt client is unavailable.");
                var hash = row.Key[(clientName.Length + 1)..^14];
                var client = _qbitManager.GetClient(clientName);
                if (client == null) throw new InvalidOperationException("Receipt client is disconnected.");
                if (!inventories.TryGetValue(clientName, out var hashes))
                {
                    hashes = (await client.GetTorrentsAsync(cancellationToken: budget.Token))
                        .Select(torrent => torrent.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    inventories[clientName] = hashes;
                }
                if (hashes.Contains(hash))
                {
                    if (_issues != null) await _issues.ResolveAsync(issue, budget.Token);
                    continue;
                }
                var receipt = ImportRescueReceipt.Parse(row.Data);
                var missing = new TorrentInfo
                {
                    Hash = hash,
                    ClientInstanceId = clientName,
                    Category = category,
                    AddedOn = receipt.AddedOn,
                    ContentPath = receipt.ContentPath,
                    SavePath = receipt.SavePath
                };
                var outcome = await TryRescueImportCoreAsync(missing, category, allowSubmission: false, allowMissing: true, budget.Token);
                if (outcome == ImportRescueOutcome.Confirmed && _config.Revision == _config.LatestRevision)
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE torrentlibrary SET imported = 1, importsubmittedat = NULL
                        WHERE hash = {hash} COLLATE NOCASE AND qbitinstance = {clientName} COLLATE NOCASE
                            AND arrinstance = {instance} COLLATE NOCASE AND torrentaddedon = {receipt.AddedOn}
                        """, budget.Token);
                if (_issues != null) await _issues.ResolveAsync(issue, budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Import rescue reconciliation held for {Instance}; error={ErrorType}", DiagnosticText.Safe(instance), ex.GetType().Name);
                if (_issues != null) await _issues.ReportAsync(issue, ct);
            }
        }
        if (_config.Revision == _config.LatestRevision) MaintenancePersistence.Write(_dbContext, cursorKey, cursor);
    }

    private async Task<ImportRescueOutcome> TryRescueImportCoreAsync(TorrentInfo torrent, string category,
        bool allowSubmission, bool allowMissing, CancellationToken ct)
    {
        using var configuration = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        if (_config.Revision != _config.LatestRevision) return ImportRescueOutcome.Pending;
        var instance = CategoryOwnershipHelper.FindArrSectionForCategory(_config, category);
        if (instance == null || !_config.ArrInstances.TryGetValue(instance, out var arr)) return ImportRescueOutcome.NotApplicable;
        var settings = _config.GetTorrentClient(torrent.ClientInstanceId);
        var key = ImportRescueReceipt.Key(torrent);
        var previous = await _dbContext.MaintenanceState.AsNoTracking().Where(row => row.Key == key).Select(row => row.Data).SingleOrDefaultAsync(ct);

        var issue = new OperationalIssueReport("import.rescue", "automation", "Import rescue needs attention",
            "A supported import rescue is pending or could not be verified. Competing failed outcomes are held.",
            "Review Arr import history, its queue, and the mapped payload before changing failure policy.",
            ArrInstance: instance, ClientInstance: torrent.ClientInstanceId, SubjectType: "torrent", SubjectId: torrent.Hash.ToUpperInvariant());
        ImportRescueReceipt receipt;
        try { receipt = previous == null ? new() : ImportRescueReceipt.Parse(previous); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            if (_issues != null) await _issues.ReportAsync(issue with
            { Impact = "Stored import rescue evidence is incomplete or unreadable. Its claim is preserved; restore valid database evidence before automatic removal." }, ct);
            return ImportRescueOutcome.Pending;
        }
        var sameEpoch = arr.Type.Equals("radarr", StringComparison.OrdinalIgnoreCase)
            && receipt.AddedOn == torrent.AddedOn && receipt.Server == ArrActivityTracking.ServerKey(arr);
        var sameDownload = sameEpoch && ClientPathKey(receipt.ContentPath) == ClientPathKey(torrent.ContentPath)
            && ClientPathKey(receipt.SavePath) == ClientPathKey(torrent.SavePath);
        if (sameDownload && receipt.Stage == "confirmed") return ImportRescueOutcome.Confirmed;
        if (!allowSubmission && receipt.Stage != "pending") return ImportRescueOutcome.NotApplicable;
        if (receipt.Stage != "pending" && (!arr.Torrent.RescueBlockedImports || !arr.Type.Equals("radarr", StringComparison.OrdinalIgnoreCase)))
            return ImportRescueOutcome.NotApplicable;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        try
        {
            if (receipt.Stage == "pending" && (receipt.Server != ArrActivityTracking.ServerKey(arr)
                || !arr.Type.Equals("radarr", StringComparison.OrdinalIgnoreCase)))
            {
                if (_issues != null) await _issues.ReportAsync(issue with { Impact = "The pending rescue belongs to a previous Arr server configuration. Restore that server configuration to reconcile its import outcome." }, token);
                return ImportRescueOutcome.Pending;
            }
            using var client = new RadarrClient(arr.URI, arr.APIKey, arr.SkipTLSVerify);
            var queue = await client.GetAllQueueAsync(token);
            var matching = queue.Where(item => string.Equals(item.DownloadId, torrent.Hash, StringComparison.OrdinalIgnoreCase)).ToArray();
            var now = _rescueClock.GetUtcNow().ToUnixTimeSeconds();
            if (receipt.Stage == "pending")
            {
                if (sameDownload)
                {
                    var since = DateTimeOffset.FromUnixTimeSeconds(receipt.SubmittedAt);
                    var history = await ArrActivityCollector.ReadAsync(arr,
                        $"history/since?date={Uri.EscapeDataString(since.ToString("O"))}&eventType=3", token);
                    var operation = new ArrActivityOperation { Type = "radarr", DownloadId = torrent.Hash, SubmittedAt = receipt.SubmittedAt };
                    if (history.Any(item => item["movieId"]?.Value<int?>() == receipt.MovieId
                        && ArrActivityCollector.PositiveImport(item, operation)
                        && (item["data"]?["droppedPath"]?.Value<string>() is not { Length: > 0 } dropped
                            || ClientPathKey(dropped) == ClientPathKey(receipt.Path))) && matching.Length == 0
                        && !await RescueImportActiveAsync(client, instance, receipt, torrent, token)
                        && await RescueTorrentUnchangedAsync(torrent, token, allowMissing))
                    {
                        receipt.Stage = "confirmed";
                        if (!await StoreRescueAsync(key, receipt, previous, token)) return ImportRescueOutcome.Pending;
                        if (_issues != null) await _issues.ResolveAsync(issue, token);
                        _logger.LogInformation("Import rescue confirmed by Arr history for {Hash} on {Instance}", torrent.Hash, instance);
                        return ImportRescueOutcome.Confirmed;
                    }
                }
                if (now - receipt.SubmittedAt < 15 * 60) return ImportRescueOutcome.Pending;
                if (!await RescueTorrentUnchangedAsync(torrent, token, allowMissing)) return ImportRescueOutcome.Pending;
                if (matching.Any(item => string.Equals(item.TrackedDownloadState, "importing", StringComparison.OrdinalIgnoreCase)))
                    return ImportRescueOutcome.Pending;
                if (await RescueImportActiveAsync(client, instance, receipt, torrent, token))
                    return ImportRescueOutcome.Pending;
                receipt.Stage = "unconfirmed";
                if (!await StoreRescueAsync(key, receipt, previous, token)) return ImportRescueOutcome.Pending;
                if (_issues != null) await _issues.ReportAsync(issue with { Impact = "The import rescue confirmation window expired. Success was not observed; fresh failure handling may resume." }, token);
                _logger.LogWarning("Import rescue confirmation expired for {Hash} on {Instance}; fresh failure handling may resume", torrent.Hash, instance);
                return ImportRescueOutcome.Unconfirmed;
            }

            if (matching.Length != 1 || !SupportedRescue(matching[0]))
            {
                if (sameEpoch && receipt.Stage == "unconfirmed" && matching.Length == 1
                    && matching[0].MovieId == receipt.MovieId && matching[0].SizeLeft == 0
                    && string.Equals(matching[0].Status, "completed", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(matching[0].TrackedDownloadStatus, "ok", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(matching[0].TrackedDownloadState, "importPending", StringComparison.OrdinalIgnoreCase)
                    && RescueOutputMatches(torrent, matching[0].OutputPath, settings?.Maintenance.PathMappings ?? []))
                {
                    receipt.Stage = "clear";
                    if (!await StoreRescueAsync(key, receipt, previous, token)) return ImportRescueOutcome.Pending;
                }
                return ImportRescueOutcome.NotApplicable;
            }
            if (sameEpoch && receipt.Stage is not ("" or "clear")) return ImportRescueOutcome.Unconfirmed;
            if (!CategoryOwnershipHelper.ArrOwnsTorrent(_config, arr, torrent) || torrent.AddedOn <= 0
                || torrent.Progress < 1 || torrent.AmountLeft != 0 || torrent.CompletionOn <= 0
                || now - torrent.CompletionOn < 60 || !TorrentPolicyHelper.IsQueueSeedingForSort(torrent.State))
                return ImportRescueOutcome.NotApplicable;
            var torrentClient = _qbitManager.GetClient(torrent.ClientInstanceId);
            if (settings == null || torrentClient == null || string.IsNullOrWhiteSpace(torrent.ContentPath)
                || settings.Maintenance.PathMappings.Count == 0)
                return ImportRescueOutcome.NotApplicable;
            if (!RescueOutputMatches(torrent, matching[0].OutputPath, settings.Maintenance.PathMappings)) return ImportRescueOutcome.NotApplicable;
            var filters = _sharedFilters?.Capture(arr.Torrent);
            if (arr.Torrent.SharedFileFilters.Count > 0 && filters?.Available != true) return ImportRescueOutcome.Pending;
            var files = await torrentClient.GetTorrentFilesAsync(torrent.Hash, token);
            var preview = await client.GetManualImportsAsync(torrent.Hash, token);
            var prepared = PrepareRescueFile(torrent, matching[0], arr.Torrent, settings.Maintenance.PathMappings, files, preview, filters, token);
            if (prepared == null) return ImportRescueOutcome.NotApplicable;
            if (!await RescueTorrentUnchangedAsync(torrent, token)) return ImportRescueOutcome.NotApplicable;
            var freshQueue = (await client.GetAllQueueAsync(token)).Where(item => string.Equals(item.DownloadId, torrent.Hash, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (freshQueue.Length != 1 || freshQueue[0].Id != matching[0].Id || freshQueue[0].MovieId != matching[0].MovieId
                || !SupportedRescue(freshQueue[0]) || !RescueOutputMatches(torrent, freshQueue[0].OutputPath, settings.Maintenance.PathMappings))
                return ImportRescueOutcome.NotApplicable;
            var currentFile = new FileInfo(prepared.LocalPath);
            if (!currentFile.Exists || currentFile.Length != prepared.Size || currentFile.LastWriteTimeUtc.Ticks != prepared.Modified)
                return ImportRescueOutcome.NotApplicable;
            var claim = new ImportRescueReceipt
            {
                AddedOn = torrent.AddedOn,
                Server = ArrActivityTracking.ServerKey(arr),
                MovieId = matching[0].MovieId!.Value,
                Path = prepared.File["path"]!.Value<string>()!,
                ContentPath = torrent.ContentPath,
                SavePath = torrent.SavePath,
                SubmittedAt = _rescueClock.GetUtcNow().ToUnixTimeSeconds(),
                Stage = "pending"
            };
            await using (var transaction = await _dbContext.Database.BeginTransactionAsync(token))
            {
                if (FailedTorrentReceipt.BlocksRemoval(_dbContext, torrent)
                    || !await StoreRescueAsync(key, claim, previous, token)) return ImportRescueOutcome.Pending;
                await transaction.CommitAsync(token);
            }
            if (_config.Revision != _config.LatestRevision) return ImportRescueOutcome.Pending;
            using var clientActivity = ArrActivityTracking.ForClient(torrent.ClientInstanceId);
            using var activity = ArrActivityTracking.Begin(_dbContext, _logger, instance, arr, "import", torrent.Hash);
            var command = await client.ImportMatchedFileAsync(prepared.File, token);
            _logger.LogInformation("Import rescue submitted for {Hash} on {Instance}; command={CommandId}; confirmation is pending", torrent.Hash, instance, command?.Id);
            return ImportRescueOutcome.Pending;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var reason = ex is JsonException or FormatException or InvalidCastException or InvalidOperationException
                ? "Arr rescue evidence was unavailable or malformed." : DiagnosticText.Error(ex);
            if (_issues != null) await _issues.ReportAsync(issue.WithEvidence(reason), ct);
            _logger.LogWarning("Import rescue could not be verified for {Hash} on {Instance}: {Reason}", torrent.Hash, instance, reason);
            return ImportRescueOutcome.Pending;
        }
    }

    private async Task<bool> RescueImportActiveAsync(RadarrClient client, string instance, ImportRescueReceipt receipt,
        TorrentInfo torrent, CancellationToken token)
    {
        var knownCommands = await _dbContext.ArrActivityOperations.AsNoTracking()
            .Where(operation => operation.Instance == instance && operation.Server == receipt.Server
                && operation.ClientInstance == torrent.ClientInstanceId && operation.DownloadId == torrent.Hash.ToUpperInvariant()
                && operation.Kind == "import" && operation.SubmittedAt >= receipt.SubmittedAt)
            .Select(operation => operation.CommandId).ToListAsync(token);
        var commands = await client.GetCommandsAsync(token);
        return commands.Any(command =>
            knownCommands.Contains(command.Id)
                && command.Status?.ToLowerInvariant() is not ("completed" or "failed" or "aborted" or "cancelled" or "canceled")
            || knownCommands.Count == 0 && (string.Equals(command.Name, "ManualImport", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(command.Name))
                && SearchExecutor.IsActiveCommandStatus(command.Status));
    }

    private async Task<bool> StoreRescueAsync(string key, ImportRescueReceipt receipt, string? previous, CancellationToken ct)
    {
        if (_config.Revision != _config.LatestRevision) return false;
        var data = JsonConvert.SerializeObject(receipt);
        return await _dbContext.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO torrentarr_maintenance_state (Key,Data) VALUES ({key},{data}) ON CONFLICT(Key) DO UPDATE SET Data=excluded.Data WHERE Data={previous ?? ""}", ct) == 1;
    }

    private async Task<bool> RescueTorrentUnchangedAsync(TorrentInfo expected, CancellationToken ct, bool allowMissing = false)
    {
        var matches = new List<TorrentInfo>();
        var sourceObserved = false;
        foreach (var (id, settings) in _config.GetAllTorrentClients())
        {
            if (string.IsNullOrWhiteSpace(settings.Host) || settings.Host == "CHANGE_ME") continue;
            if (settings.Disabled) return false;
            var client = _qbitManager.GetClient(id);
            if (client == null) return false;
            if (id.Equals(expected.ClientInstanceId, StringComparison.OrdinalIgnoreCase)) sourceObserved = true;
            foreach (var current in await client.GetTorrentsAsync(cancellationToken: ct))
                if (current.Hash.Equals(expected.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    current.ClientInstanceId = id;
                    matches.Add(current);
                }
        }
        if (!sourceObserved) return false;
        if (matches.Count == 0) return allowMissing;
        return matches.Count == 1 && matches[0].ClientInstanceId == expected.ClientInstanceId
            && matches[0].AddedOn == expected.AddedOn && matches[0].Category == expected.Category
            && ClientPathKey(matches[0].ContentPath) == ClientPathKey(expected.ContentPath)
            && ClientPathKey(matches[0].SavePath) == ClientPathKey(expected.SavePath)
            && matches[0].Progress >= 1 && matches[0].AmountLeft == 0;
    }

    private static bool RescueOutputMatches(TorrentInfo torrent, string? outputPath, IReadOnlyList<PathMappingConfig> mappings)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return false;
        var local = new PathMappingService().Resolve(torrent.ContentPath, mappings).LocalPath;
        var output = ClientPathKey(outputPath);
        return output == ClientPathKey(torrent.ContentPath) || local != null && output == ClientPathKey(local);
    }

    private static string ClientPathKey(string? path)
    {
        var normalized = (path ?? "").Replace('\\', '/').TrimEnd('/');
        return normalized.Length > 1 && normalized[1] == ':' || normalized.StartsWith("//", StringComparison.Ordinal)
            ? normalized.ToUpperInvariant() : normalized;
    }

    internal sealed record RescueFile(JObject File, string LocalPath, long Size, long Modified);
    private sealed record RescueOwnedFile(TorrentFileRecord File, string? LocalPath, string? LocalRoot);
    internal static RescueFile? PrepareRescueFile(TorrentInfo torrent, QueueItem queue, TorrentConfig policy,
        IReadOnlyList<PathMappingConfig> mappings, IReadOnlyList<TorrentFileRecord> files, JArray preview, SharedFileFilterService.View? filters, CancellationToken ct = default)
    {
        if (preview.Count > 1000 || policy.SharedFileFilters.Count > 0 && filters?.Available != true
            || preview.Any(item => item is not JObject resource || resource["path"]?.Type != JTokenType.String
                || string.IsNullOrWhiteSpace(resource["path"]!.Value<string>()))) return null;
        var paths = new PathMappingService();
        var options = policy.CaseSensitiveMatches ? RegexOptions.None : RegexOptions.IgnoreCase;
        var eligible = new Dictionary<string, RescueOwnedFile>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var name = file.Name.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is "." or "..")) return null;
            if (FileFilterMatcher.LocalReason(name, policy, options) != null || filters?.Match(name) != null) continue;
            var path = torrent.SavePath.TrimEnd('/', '\\') + "/" + name;
            var mapping = paths.Resolve(path, mappings);
            var mappedFile = new RescueOwnedFile(file, mapping.LocalPath, mapping.LocalRoot);
            foreach (var alias in new[] { path, mapping.LocalPath }.OfType<string>())
            {
                var key = ClientPathKey(alias);
                if (eligible.TryGetValue(key, out var existing) && existing != mappedFile) return null;
                eligible[key] = mappedFile;
            }
        }
        var candidates = preview.OfType<JObject>().Where(item => eligible.ContainsKey(ClientPathKey(item["path"]?.Value<string>()))).ToArray();
        if (candidates.Length != 1) return null;
        var candidate = candidates[0];
        var candidatePath = ClientPathKey(candidate["path"]?.Value<string>());
        var contentPath = ClientPathKey(queue.OutputPath);
        if (candidatePath != contentPath && !candidatePath.StartsWith(contentPath + "/", StringComparison.Ordinal)) return null;
        if (candidate["movie"]?["id"]?.Value<int?>() != queue.MovieId || candidate["movieFileId"]?.Value<int?>() > 0
            || !string.Equals(candidate["downloadId"]?.Value<string>(), torrent.Hash, StringComparison.OrdinalIgnoreCase)
            || candidate["rejections"] is not JArray { Count: 0 } || candidate["quality"] is not JObject
            || candidate["quality"]?["quality"]?["id"]?.Value<int?>() is not > 0 || candidate["languages"] is not JArray { Count: > 0 }) return null;
        var owned = eligible[candidatePath];
        if (owned.File.Progress is not >= 1 || owned.LocalPath == null || owned.LocalRoot == null
            || paths.ValidateLocalPath(owned.LocalPath, owned.LocalRoot, true) != null) return null;
        var info = new FileInfo(owned.LocalPath);
        if (!info.Exists || info.Length <= 0 || owned.File.Size <= 0 || info.Length != owned.File.Size
            || candidate["size"]?.Value<long?>() != info.Length) return null;
        var payload = new JObject
        {
            ["path"] = candidate["path"]!.DeepClone(),
            ["movieId"] = queue.MovieId,
            ["downloadId"] = torrent.Hash,
            ["quality"] = candidate["quality"]!.DeepClone(),
            ["languages"] = candidate["languages"]!.DeepClone()
        };
        foreach (var field in new[] { "releaseGroup", "folderName", "indexerFlags" })
            if (candidate[field] != null) payload[field] = candidate[field]!.DeepClone();
        return new(payload, info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
    }
}
