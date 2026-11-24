using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Services;

internal sealed class ImportPlanFile
{
    public string Path { get; set; } = "";
    public string Source { get; set; } = "";
    public string OwnedPath { get; set; } = "";
    public string PayloadStamp { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string State { get; set; } = "blocked";
    public string Reason { get; set; } = "";
    public int[] MediaIds { get; set; } = [];
    public int ParentId { get; set; }
    public int AlbumId { get; set; }
    public int? IncomingScore { get; set; }
    public int?[] ExistingScores { get; set; } = [];
    public long SubmittedAt { get; set; }
    public int? CommandId { get; set; }
}

internal sealed class ImportPlanReceipt
{
    public string Hash { get; set; } = "";
    public string Client { get; set; } = "";
    public string Instance { get; set; } = "";
    public string Server { get; set; } = "";
    public string Type { get; set; } = "";
    public long AddedOn { get; set; }
    public string ContentPath { get; set; } = "";
    public string SavePath { get; set; } = "";
    public long CheckedAt { get; set; }
    public string State { get; set; } = "blocked";
    public List<ImportPlanFile> Files { get; set; } = [];
    internal static string Key(TorrentInfo torrent) => MaintenancePersistence.Key(torrent.ClientInstanceId, torrent.Hash, "import-plan");
    internal static ImportPlanReceipt Parse(string json)
    {
        var value = JsonConvert.DeserializeObject<ImportPlanReceipt>(json);
        if (value == null || value.AddedOn <= 0 || string.IsNullOrWhiteSpace(value.Hash)
            || string.IsNullOrWhiteSpace(value.Client) || string.IsNullOrWhiteSpace(value.Instance)
            || value.Server is not { Length: 64 } || !value.Server.All(Uri.IsHexDigit)
            || value.Type is not ("radarr" or "sonarr" or "lidarr" or "readarr")
            || value.State is not ("blocked" or "pending" or "partial" or "confirmed")
            || string.IsNullOrWhiteSpace(value.ContentPath) || string.IsNullOrWhiteSpace(value.SavePath)
            || value.Files == null || value.Files.Any(file => file == null
                || file.State is not ("blocked" or "ready" or "pending" or "failed" or "confirmed")
                || file.MediaIds == null || file.ExistingScores == null || file.Source == null || file.Path == null
                || file.Fingerprint == null || file.OwnedPath == null || file.Reason == null
                || file.State is "pending" or "confirmed" && (file.SubmittedAt <= 0 || file.MediaIds.Length == 0 || file.Source.Length == 0)))
            throw new InvalidDataException("Invalid import plan; preserving its submission claims.");
        var expected = value.Files.Any(file => file.State == "pending") ? "pending"
            : value.Files.Count > 0 && value.Files.All(file => file.State == "confirmed") ? "confirmed"
            : value.Files.Any(file => file.State == "confirmed") ? "partial" : "blocked";
        if (expected != value.State) throw new InvalidDataException("Import plan outcome is inconsistent with its files.");
        return value;
    }

    internal static bool BlocksRemoval(TorrentarrDbContext db, TorrentInfo torrent, TorrentRemovalIntent intent)
    {
        var key = Key(torrent);
        var json = db.MaintenanceState.AsNoTracking().Where(row => row.Key == key).Select(row => row.Data).SingleOrDefault();
        if (json == null) return false;
        try
        {
            var receipt = Parse(json);
            if (receipt.Files.Any(file => file.State == "pending")) return true;
            if (receipt.AddedOn != torrent.AddedOn) return false;
            // A partial import never authorizes deletion of its remaining payload.
            return receipt.State != "confirmed" || intent is TorrentRemovalIntent.StalledDownload
                or TorrentRemovalIntent.RejectedByAllowlist or TorrentRemovalIntent.RejectedByCustomFormat;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return true; }
    }
}

public partial class ArrImportService
{
    private sealed record PreparedImport(ImportPlanFile Decision, JObject? Payload, string? LocalPath, long Size, long Modified);

    public async Task<ImportResult> TriggerImportAsync(TorrentInfo torrent, string category, CancellationToken cancellationToken = default)
    {
        using var configuration = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        var instance = CategoryOwnershipHelper.FindArrSectionForCategory(_config, category);
        if (instance == null || !_config.ArrInstances.TryGetValue(instance, out var arr))
            return new() { Message = $"No Arr instance configured for category {category}" };
        using var activityClient = ArrActivityTracking.ForClient(torrent.ClientInstanceId);
        using var activity = ArrActivityTracking.Begin(_dbContext, _logger, instance, arr, "import", torrent.Hash);
        var key = ImportPlanReceipt.Key(torrent);
        var previous = await _dbContext.MaintenanceState.AsNoTracking().Where(row => row.Key == key).Select(row => row.Data).SingleOrDefaultAsync(cancellationToken);
        ImportPlanReceipt? receipt = null;
        var readable = previous == null;
        try
        {
            var now = _rescueClock.GetUtcNow().ToUnixTimeSeconds();
            receipt = previous == null ? null : ImportPlanReceipt.Parse(previous);
            readable = true;
            if (_config.Revision != _config.LatestRevision) return Held("Configuration activation is pending.");
            if (receipt != null && receipt.Files.Any(file => file.State == "pending"))
                return Held("Import submission is awaiting reconciliation.");
            var same = receipt != null && receipt.AddedOn == torrent.AddedOn && receipt.Server == ArrActivityTracking.ServerKey(arr)
                && receipt.Instance == instance && ClientPathKey(receipt.ContentPath) == ClientPathKey(torrent.ContentPath)
                && ClientPathKey(receipt.SavePath) == ClientPathKey(torrent.SavePath);
            if (same && now - receipt!.CheckedAt < 900) return Held("Import eligibility is cooling down.");
            if (!CategoryOwnershipHelper.ArrOwnsTorrent(_config, arr, torrent) || torrent.AddedOn <= 0
                || torrent.Progress < 1 || torrent.AmountLeft != 0 || torrent.CompletionOn <= 0
                || now - torrent.CompletionOn < 60 || !TorrentPolicyHelper.IsQueueSeedingForSort(torrent.State))
                return Held("Import requires an owned, completed and stable torrent.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var ct = timeout.Token;
            using var client = ArrImportClient.Create(arr);
            var queue = await ReadImportQueueAsync(client, torrent.Hash, ct);
            if (queue.Any(item => string.Equals(item["trackedDownloadState"]?.Value<string>(), "importing", StringComparison.OrdinalIgnoreCase)))
                return Held("Arr is already importing this download.");
            var previewResource = $"manualimport?downloadId={Uri.EscapeDataString(torrent.Hash)}&filterExistingFiles=true&replaceExistingFiles=true";
            var preview = await client.ReadImportAsync(previewResource, ct) as JArray
                ?? throw new IOException("Invalid manual import preview.");
            var torrentClient = _qbitManager.GetClient(torrent.ClientInstanceId) ?? throw new IOException("Torrent client is unavailable.");
            var files = await torrentClient.GetTorrentFilesAsync(torrent.Hash, ct);
            var prepared = await PrepareImportFilesAsync(torrent, arr, client, queue, files, preview, ct);
            var next = new ImportPlanReceipt
            {
                Hash = torrent.Hash,
                Client = torrent.ClientInstanceId,
                Instance = instance,
                Type = arr.Type.ToLowerInvariant(),
                Server = ArrActivityTracking.ServerKey(arr),
                AddedOn = torrent.AddedOn,
                ContentPath = torrent.ContentPath,
                SavePath = torrent.SavePath,
                CheckedAt = now
            };
            foreach (var item in prepared)
            {
                var old = same ? receipt!.Files.FirstOrDefault(file => file.State == "confirmed" && file.OwnedPath == item.Decision.OwnedPath
                        && file.PayloadStamp == item.Decision.PayloadStamp && file.PayloadStamp.Length > 0
                        && (item.Decision.Source.Length == 0 || item.Decision.Source == file.Source)
                    || file.Source == item.Decision.Source && file.Source.Length > 0) : null;
                if (old?.State == "confirmed")
                {
                    item.Decision.State = "confirmed";
                    next.Files.Add(old);
                    continue;
                }
                else if (old?.State == "failed" && old.Fingerprint == item.Decision.Fingerprint)
                {
                    item.Decision.State = "failed";
                    item.Decision.Reason = old.Reason;
                    item.Decision.SubmittedAt = old.SubmittedAt;
                    item.Decision.CommandId = old.CommandId;
                }
                next.Files.Add(item.Decision);
            }
            // Keep confirmation evidence even when Arr no longer includes an imported source in its preview.
            if (same) next.Files.AddRange(receipt!.Files.Where(old => old.State == "confirmed"
                && !next.Files.Any(file => file.Source == old.Source)));
            var ready = prepared.Where(item => item.Decision.State == "ready").ToArray();
            if (ready.Length > 0)
            {
                if (!await RescueTorrentUnchangedAsync(torrent, ct)) return Held("Torrent identity changed or is ambiguous across clients.");
                foreach (var item in ready)
                {
                    var info = new FileInfo(item.LocalPath!);
                    if (!info.Exists || info.Length != item.Size || info.LastWriteTimeUtc.Ticks != item.Modified)
                        return Held("Payload changed during import preflight.");
                }
                // Refresh library evidence immediately before claiming a replacement.
                var freshQueue = await ReadImportQueueAsync(client, torrent.Hash, ct);
                if (freshQueue.Any(item => string.Equals(item["trackedDownloadState"]?.Value<string>(), "importing", StringComparison.OrdinalIgnoreCase)))
                    return Held("Arr started importing during preflight.");
                var freshPreview = await client.ReadImportAsync(previewResource, ct) as JArray ?? throw new IOException("Invalid refreshed import preview.");
                var refreshed = await PrepareImportFilesAsync(torrent, arr, client, freshQueue,
                    await torrentClient.GetTorrentFilesAsync(torrent.Hash, ct), freshPreview, ct);
                if (ready.Any(item => !refreshed.Any(fresh => fresh.Decision.State == "ready"
                    && fresh.Decision.Fingerprint == item.Decision.Fingerprint))) return Held("Import eligibility changed during preflight.");
                var commands = await client.ReadImportAsync("command", ct) as JArray ?? throw new IOException("Invalid command inventory.");
                if (commands.OfType<JObject>().Any(command => SearchExecutor.IsActiveCommandStatus(command["status"]?.Value<string>() ?? "")
                    && (string.Equals(command["body"]?["downloadClientId"]?.Value<string>(), torrent.Hash, StringComparison.OrdinalIgnoreCase)
                        || command["body"]?["files"] is JArray activeFiles && activeFiles.Any(file =>
                            string.Equals(file["downloadId"]?.Value<string>(), torrent.Hash, StringComparison.OrdinalIgnoreCase)))))
                    return Held("An import command is already active for this download.");
                foreach (var item in ready) { item.Decision.State = "pending"; item.Decision.SubmittedAt = now; }
            }
            SetPlanState(next);
            if (!await StoreImportPlanAsync(key, next, previous, ct)) return Held("Another observer owns this import decision.");
            await ObserveAsync("preflight", category, torrent.Hash, true, ct);
            if (receipt == null || receipt.State != next.State || JsonConvert.SerializeObject(receipt.Files) != JsonConvert.SerializeObject(next.Files))
                await RecordImportPlanAsync(torrent, next, next.State);
            if (ready.Length == 0) return Held("No new eligible files: " + string.Join("; ", next.Files.Select(file => file.Reason).Where(reason => reason.Length > 0).Distinct()));
            // Claim first, then POST once. Any exception leaves a durable pending claim.
            var commandId = await client.SubmitImportAsync(new JArray(ready.Select(item => item.Payload!)),
                arr.Torrent.RescueBlockedImports ? "Copy" : ResolveImportMode(arr, _config.Settings), ct);
            var claimed = JsonConvert.SerializeObject(next);
            foreach (var item in ready) item.Decision.CommandId = commandId;
            await StoreImportPlanAsync(key, next, claimed, ct);
            return new() { Success = true, CommandId = commandId, Message = $"ManualImport {commandId} submitted for {ready.Length} files; awaiting confirmation" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (readable && torrent.AddedOn > 0 && !string.IsNullOrWhiteSpace(torrent.ContentPath) && !string.IsNullOrWhiteSpace(torrent.SavePath))
            {
                var blocked = receipt ?? new ImportPlanReceipt
                {
                    Hash = torrent.Hash,
                    Client = torrent.ClientInstanceId,
                    Instance = instance,
                    Type = arr.Type.ToLowerInvariant(),
                    Server = ArrActivityTracking.ServerKey(arr),
                    AddedOn = torrent.AddedOn,
                    ContentPath = torrent.ContentPath,
                    SavePath = torrent.SavePath,
                    CheckedAt = _rescueClock.GetUtcNow().ToUnixTimeSeconds(),
                    Files = [new() { Reason = "Import evidence is unavailable: " + DiagnosticText.Error(ex) }]
                };
                blocked.CheckedAt = _rescueClock.GetUtcNow().ToUnixTimeSeconds();
                try
                {
                    if (await StoreImportPlanAsync(key, blocked, previous, cancellationToken) && receipt == null)
                        await RecordImportPlanAsync(torrent, blocked, "blocked");
                }
                catch (Exception persistenceError) when (persistenceError is not OperationCanceledException)
                { _logger.LogWarning("Import decision persistence unavailable: {Error}", DiagnosticText.Error(persistenceError)); }
            }
            await ObserveAsync("preflight", category, torrent.Hash, false, cancellationToken, DiagnosticText.Error(ex));
            _logger.LogWarning("Import preflight held for {Hash}: {Error}", torrent.Hash, DiagnosticText.Error(ex));
            return Held("Import evidence or submission is unavailable; saved claims are retained.");
        }
    }

    private static ImportResult Held(string reason) => new() { Deferred = true, Message = reason };

    private async Task<bool> StoreImportPlanAsync(string key, ImportPlanReceipt receipt, string? previous, CancellationToken ct)
    {
        if (_config.Revision != _config.LatestRevision || !_dbContext.Database.IsSqlite()) return false;
        var data = JsonConvert.SerializeObject(receipt);
        return await _dbContext.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO torrentarr_maintenance_state (Key,Data) VALUES ({key},{data}) ON CONFLICT(Key) DO UPDATE SET Data=excluded.Data WHERE Data={previous ?? ""}", ct) == 1;
    }

    private static void SetPlanState(ImportPlanReceipt receipt) => receipt.State = receipt.Files.Any(file => file.State == "pending") ? "pending"
        : receipt.Files.Count > 0 && receipt.Files.All(file => file.State == "confirmed") ? "confirmed"
        : receipt.Files.Any(file => file.State == "confirmed") ? "partial" : "blocked";

    private static Task RecordImportPlanAsync(TorrentInfo torrent, ImportPlanReceipt receipt, string outcome) => WorkerHistory.RecordAsync(new()
    {
        Type = "import",
        SubjectType = "torrent",
        SubjectId = torrent.Hash,
        ClientInstance = torrent.ClientInstanceId,
        Summary = "Import " + outcome + " · " + torrent.Name,
        Outcome = outcome,
        Details = new JObject { ["method"] = "ManualImport", ["files"] = JArray.FromObject(receipt.Files.Take(500)) }.ToString(Formatting.None)
    });

    private static async Task<JArray> ReadImportQueueAsync(IArrImportClient client, string hash, CancellationToken ct)
    {
        var records = await ArrQueuePaging.ReadAllAsync<JObject>(async (page, size) =>
        {
            var response = await client.ReadImportAsync($"queue?page={page}&pageSize={size}&includeUnknownSeriesItems=true&includeUnknownMovieItems=true", ct);
            if (response is not JObject || response["records"] is not JArray items || items.Any(item => item is not JObject)
                || response["page"]?.Value<int?>() is not > 0 || response["pageSize"]?.Value<int?>() is not > 0
                || response["totalRecords"]?.Value<int?>() is not >= 0) throw new IOException("Invalid Arr queue evidence.");
            return (response["page"]!.Value<int>(), response["pageSize"]!.Value<int>(), response["totalRecords"]!.Value<int>(), items.Cast<JObject>().ToList());
        }, item => item["id"]?.Value<int>() ?? throw new IOException("Queue identity is missing."), ct);
        return new JArray(records.Where(item => string.Equals(item["downloadId"]?.Value<string>(), hash, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<List<PreparedImport>> PrepareImportFilesAsync(TorrentInfo torrent, ArrInstanceConfig arr, IArrImportClient client,
        JArray queue, IReadOnlyList<TorrentFileRecord> inventory, JArray preview, CancellationToken ct)
    {
        if (preview.Count > 1000 || inventory.Count > 10000 || preview.Any(item => item is not JObject))
            throw new IOException("Invalid or excessive import inventory.");
        if (inventory.Select(file => ClientPathKey(file.Name)).Distinct(StringComparer.Ordinal).Count() != inventory.Count)
            throw new IOException("Torrent inventory repeats a file path.");
        var filters = _sharedFilters?.Capture(arr.Torrent);
        if (arr.Torrent.SharedFileFilters.Count > 0 && filters?.Available != true) throw new IOException("Shared file filters are unavailable.");
        var settings = _config.GetTorrentClient(torrent.ClientInstanceId) ?? throw new IOException("Client settings are unavailable.");
        var paths = new PathMappingService();
        var type = arr.Type.ToLowerInvariant();
        var result = new List<PreparedImport>();
        var evidence = new Dictionary<string, JToken>(StringComparer.Ordinal);
        async Task<JToken> Read(string resource)
        {
            if (!evidence.TryGetValue(resource, out var value)) evidence[resource] = value = await client.ReadImportAsync(resource, ct);
            return value;
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in inventory)
        {
            ct.ThrowIfCancellationRequested();
            var name = file.Name.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is "." or ".."))
                throw new IOException("Torrent file path escapes its download root.");
            var options = arr.Torrent.CaseSensitiveMatches ? RegexOptions.None : RegexOptions.IgnoreCase;
            if (file.Priority == 0 || FileFilterMatcher.LocalReason(name, arr.Torrent, options) != null || filters?.Match(name) != null) continue;
            var suffix = Path.GetExtension(name).ToLowerInvariant();
            var media = type is "radarr" or "sonarr" ? new[] { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg", ".vob", ".ogm", ".strm" }
                : type == "lidarr" ? new[] { ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".aiff", ".alac", ".ape", ".dsf", ".dff", ".wv" }
                : new[] { ".epub", ".pdf", ".mobi", ".azw", ".azw3", ".cbz", ".cbr", ".mp3", ".m4b", ".flac" };
            if (!media.Contains(suffix)) continue;
            var ownedPath = torrent.SavePath.TrimEnd('/', '\\') + "/" + name;
            var local = settings.Maintenance.PathMappings.Count > 0 ? paths.Resolve(ownedPath, settings.Maintenance.PathMappings)
                : new PathMappingResult(ownedPath, LocalRoot: torrent.SavePath);
            var aliases = new HashSet<string>(StringComparer.Ordinal) { ClientPathKey(ownedPath) };
            if (local.LocalPath != null) aliases.Add(ClientPathKey(local.LocalPath));
            var root = ClientPathKey(torrent.ContentPath);
            var owned = ClientPathKey(ownedPath);
            var relative = owned == root ? "" : owned.StartsWith(root + "/", StringComparison.Ordinal) ? owned[(root.Length + 1)..] : null;
            if (relative == null) throw new IOException("Torrent file is outside its content path.");
            foreach (var item in queue)
            {
                var output = item["outputPath"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(output)) continue;
                aliases.Add(ClientPathKey(output) + (relative.Length > 0 ? "/" + relative : ""));
                if (relative.Length == 0) aliases.Add(ClientPathKey(output) + "/" + Path.GetFileName(name));
            }
            var matches = preview.OfType<JObject>().Where(item => aliases.Contains(ClientPathKey(item["path"]?.Value<string>()))).ToArray();
            var decision = new ImportPlanFile { OwnedPath = owned, Path = matches.Length == 1 ? matches[0]["path"]!.Value<string>()! : ownedPath };
            var info = local.LocalPath == null ? null : new FileInfo(local.LocalPath);
            if (info?.Exists == true) decision.PayloadStamp = ImportFingerprint(owned, info.Length, info.LastWriteTimeUtc.Ticks);
            string? reason = file.Progress < 1 ? "File is incomplete."
                : local.LocalPath == null || local.LocalRoot == null || paths.ValidateLocalPath(local.LocalPath, local.LocalRoot, true) != null ? "Payload is missing, inaccessible or unsafe."
                : info == null || !info.Exists || info.Length <= 0 || file.Size != info.Length ? "Payload size does not match the completed torrent file."
                : matches.Length != 1 ? "Arr preview does not identify exactly one owned file."
                : !used.Add(ClientPathKey(decision.Path)) ? "Arr preview ambiguously maps multiple torrent files to one source." : null;
            JObject? payload = null;
            if (reason == null)
            {
                var candidate = matches[0];
                if (!string.Equals(candidate["downloadId"]?.Value<string>(), torrent.Hash, StringComparison.OrdinalIgnoreCase)
                    || candidate["size"]?.Value<long?>() != info!.Length || candidate["quality"]?["quality"]?["id"]?.Value<int?>() is not > 0)
                    reason = "Arr preview lacks consistent download, size or quality evidence.";
                else
                {
                    payload = await PrepareImportTargetAsync(type, candidate, decision, Read, ct);
                    if (payload == null) reason = decision.Reason;
                    else if (candidate["rejections"] is not JArray rejections) reason = "Arr rejection evidence is unavailable.";
                    else if (rejections.Count > 0 && !arr.Torrent.RescueBlockedImports)
                        reason = "Arr rejected this file: " + string.Join("; ", rejections.Select(rejection => rejection["reason"]?.Value<string>() ?? "Unknown rejection"));
                    else if (rejections.Any(rejection => !IsImportPolicyRejection(rejection["reason"]?.Value<string>())))
                        reason = "Arr reports a structural or unrecognized rejection: " + string.Join("; ", rejections.Select(rejection => rejection["reason"]?.Value<string>()));
                }
                decision.Source = ImportFingerprint(owned, info!.Length, info.LastWriteTimeUtc.Ticks, decision.ParentId, string.Join(',', decision.MediaIds));
                decision.Fingerprint = ImportFingerprint(decision.Source, candidate["quality"], candidate["languages"], candidate["releaseGroup"],
                    candidate["indexerFlags"], candidate["releaseType"], candidate["rejections"], decision.IncomingScore,
                    string.Join(',', decision.ExistingScores), decision.Fingerprint);
            }
            decision.State = reason == null ? "ready" : "blocked";
            decision.Reason = reason ?? "Identified file passes import policy.";
            result.Add(new(decision, reason == null ? payload : null, local.LocalPath, info?.Exists == true ? info.Length : 0, info?.Exists == true ? info.LastWriteTimeUtc.Ticks : 0));
        }
        var ambiguousPaths = result.GroupBy(item => ClientPathKey(item.Decision.Path))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var duplicateTargets = type == "readarr" ? new HashSet<int>() : result.Where(item => item.Payload != null)
            .SelectMany(item => item.Decision.MediaIds).GroupBy(id => id).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        foreach (var item in result.Where(item => ambiguousPaths.Contains(ClientPathKey(item.Decision.Path))
            || item.Decision.MediaIds.Any(duplicateTargets.Contains)))
        { item.Decision.State = "blocked"; item.Decision.Reason = "Multiple source files ambiguously target the same media."; }
        if (result.Count == 0) result.Add(new(new() { Reason = "No allowed media files are available for import." }, null, null, 0, 0));
        return result;
    }

    // Override selection policy, never filesystem, parsing, identity or integrity failures.
    internal static bool IsImportPolicyRejection(string? reason) => reason != null && new[]
    {
        "Not a Custom Format upgrade", "Not an upgrade for existing", "Not a quality upgrade", "Existing file meets cutoff",
        "Quality for release in queue already meets cutoff", "Release was matched to movie by ID", "Found matching movie via grab history, but release was matched to movie by ID",
        "Quality is not wanted in profile", "Quality is not allowed in this quality profile", "Custom Format score is below minimum",
        "Not a preferred word upgrade"
    }.Any(prefix => reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string ImportFingerprint(params object?[] parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(parts))));

    private static async Task<JObject?> PrepareImportTargetAsync(string type, JObject candidate, ImportPlanFile decision,
        Func<string, Task<JToken>> read, CancellationToken ct)
    {
        var parentName = type switch { "radarr" => "movie", "sonarr" => "series", "lidarr" => "artist", _ => "author" };
        var parentId = candidate[parentName]?["id"]?.Value<int?>();
        var childrenName = type switch { "sonarr" => "episodes", "lidarr" => "tracks", "readarr" => "books", _ => "" };
        var children = type is "radarr" or "readarr"
            ? new JArray(candidate[type == "radarr" ? "movie" : "book"]?.DeepClone() ?? JValue.CreateNull())
            : candidate[childrenName] as JArray;
        if (parentId is not > 0 || children is not { Count: > 0 } || children.Any(child => child["id"]?.Value<int?>() is not > 0))
        { decision.Reason = "Arr has not unambiguously identified the media."; return null; }
        var ids = children.Select(child => child["id"]!.Value<int>()).Order().ToArray();
        if (ids.Distinct().Count() != ids.Length) { decision.Reason = "Arr supplied duplicate media identities."; return null; }
        decision.ParentId = parentId.Value;
        decision.MediaIds = ids;
        var parent = await read($"{parentName}/{parentId}");
        if (parent is not JObject || parent["id"]?.Value<int?>() != parentId) throw new IOException("Missing current media identity.");
        int? profileId = parent["qualityProfileId"]?.Value<int?>();
        var profiles = await read("qualityprofile") as JArray ?? throw new IOException("Invalid quality profiles.");
        var profile = profiles.OfType<JObject>().SingleOrDefault(item => item["id"]?.Value<int?>() == profileId);
        var incoming = CustomFormatScore(candidate, profile);
        decision.IncomingScore = incoming;
        var existingScores = new List<int?>();
        var fileIds = new List<int>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var childName = type switch { "radarr" => "movie", "sonarr" => "episode", "lidarr" => "track", _ => "book" };
            var current = type == "radarr" ? parent : type == "lidarr"
                ? (await read($"track?trackIds={id}") as JArray)?.SingleOrDefault() ?? throw new IOException("Missing current track.")
                : await read($"{childName}/{id}");
            if (current is not JObject || current["id"]?.Value<int?>() != id
                || type != "radarr" && current[parentName + "Id"]?.Value<int?>() != parentId
                || type == "lidarr" && current["albumId"]?.Value<int?>() != candidate["album"]?["id"]?.Value<int?>())
                throw new IOException("Current item does not belong to its identified parent.");
            var fileIdName = childName + "FileId";
            var fileId = current[fileIdName]?.Value<int?>();
            var hasFile = current["hasFile"]?.Value<bool?>();
            JArray? bookFiles = null;
            if (type == "readarr")
            {
                bookFiles = await read($"bookfile?bookId={id}") as JArray ?? throw new IOException("Missing current book files.");
                if (bookFiles.Count == 0 && hasFile == true) throw new IOException("Book inventory is incomplete.");
            }
            if (type != "readarr" && fileId == null) throw new IOException("Missing current file identity.");
            if (hasFile == true && fileId == 0) throw new IOException("Current file inventory is inconsistent.");
            var existingFiles = bookFiles ?? (fileId > 0 ? new JArray(await read($"{childName}file/{fileId}")) : new JArray());
            foreach (var existing in existingFiles)
            {
                if (existing is not JObject || existing["id"]?.Value<int?>() is not > 0
                    || type != "readarr" && existing["id"]?.Value<int?>() != fileId) throw new IOException("Missing current file metadata.");
                fileIds.Add(existing["id"]!.Value<int>());
                existingScores.Add(CustomFormatScore(existing, profile));
            }
        }
        if (type is "sonarr" or "lidarr" && fileIds.Count > 0)
        {
            var inventoryResource = type == "sonarr" ? $"episode?seriesId={parentId}" : $"track?albumId={candidate["album"]?["id"]}";
            var siblings = await read(inventoryResource) as JArray ?? throw new IOException("Missing current media inventory.");
            var fileField = type == "sonarr" ? "episodeFileId" : "trackFileId";
            if (siblings.Any(item => fileIds.Contains(item[fileField]?.Value<int>() ?? 0) && !ids.Contains(item["id"]?.Value<int>() ?? 0)))
            { decision.Reason = "Replacing a shared library file would affect media outside this source file's targets."; return null; }
        }
        if (type == "lidarr")
        {
            var albumId = candidate["album"]?["id"]?.Value<int?>();
            if (albumId is not > 0) { decision.Reason = "Arr has not identified the album."; return null; }
            var album = await read($"album/{albumId}");
            var albumFiles = await read($"trackfile?albumId={albumId}") as JArray ?? throw new IOException("Missing current album file inventory.");
            if (album["id"]?.Value<int?>() != albumId || album["artistId"]?.Value<int?>() != parentId
                || album["releases"] is not JArray releases) throw new IOException("Missing current album release identity.");
            if (albumFiles.Count > 0 && !releases.Any(release => release["monitored"]?.Value<bool?>() == true
                && release["id"]?.Value<int?>() == candidate["albumReleaseId"]?.Value<int?>()))
            { decision.Reason = "Switching an album release would change existing track identities; review the release in Arr."; return null; }
        }
        decision.ExistingScores = existingScores.ToArray();
        decision.Fingerprint = ImportFingerprint(fileIds);
        if (existingScores.Count > 0 && (incoming == null || existingScores.Any(score => score == null)))
        { decision.Reason = "Replacement custom-format scores are unavailable."; return null; }
        if (existingScores.Any(score => incoming < score))
        { decision.Reason = "Incoming custom-format score is lower than an existing item."; return null; }
        var payload = new JObject
        {
            ["path"] = candidate["path"]!.DeepClone(),
            ["downloadId"] = candidate["downloadId"]!.DeepClone(),
            [parentName + "Id"] = parentId,
            ["quality"] = candidate["quality"]!.DeepClone()
        };
        if (type is "sonarr" or "lidarr") payload[type == "sonarr" ? "episodeIds" : "trackIds"] = new JArray(ids);
        if (type == "readarr")
        {
            if (string.IsNullOrWhiteSpace(candidate["foreignEditionId"]?.Value<string>()))
            { decision.Reason = "Arr has not identified a book edition."; return null; }
            payload["bookId"] = ids[0];
            payload["foreignEditionId"] = candidate["foreignEditionId"]!.DeepClone();
        }
        if (type == "lidarr")
        {
            if (candidate["album"]?["id"]?.Value<int?>() is not > 0 || candidate["albumReleaseId"]?.Value<int?>() is not > 0
                || children.Any(child => child["albumId"]?.Value<int?>() != candidate["album"]!["id"]!.Value<int>()))
            { decision.Reason = "Arr has not identified one album release and its tracks."; return null; }
            decision.AlbumId = candidate["album"]!["id"]!.Value<int>();
            payload["albumId"] = candidate["album"]!["id"]!.DeepClone();
            payload["albumReleaseId"] = candidate["albumReleaseId"]!.DeepClone();

        }
        foreach (var field in new[] { "languages", "releaseGroup", "folderName", "indexerFlags", "releaseType" })
            if (candidate[field] != null) payload[field] = candidate[field]!.DeepClone();
        return payload;
    }

    internal static int? CustomFormatScore(JToken file, JObject? profile)
    {
        if (file["customFormatScore"]?.Type == JTokenType.Integer) return file["customFormatScore"]!.Value<int>();
        if (file["customFormats"] is not JArray formats || profile?["formatItems"] is not JArray weights) return null;
        var score = 0;
        foreach (var format in formats)
        {
            var weight = weights.FirstOrDefault(item => item["format"]?.Value<int?>() == format["id"]?.Value<int?>());
            if (format["id"]?.Value<int?>() is not > 0 || weight?["score"]?.Type != JTokenType.Integer) return null;
            score = checked(score + weight["score"]!.Value<int>());
        }
        return score;
    }

    private async Task<ImportRescueOutcome?> ReconcileImportPlanAsync(TorrentInfo torrent, string category, CancellationToken ct, bool allowMissing = false)
    {
        var key = ImportPlanReceipt.Key(torrent);
        var previous = await _dbContext.MaintenanceState.AsNoTracking().Where(row => row.Key == key).Select(row => row.Data).SingleOrDefaultAsync(ct);
        if (previous == null) return null;
        try
        {
            var receipt = ImportPlanReceipt.Parse(previous);
            var instance = CategoryOwnershipHelper.FindArrSectionForCategory(_config, category);
            if (instance == null || !_config.ArrInstances.TryGetValue(instance, out var arr)) return ImportRescueOutcome.Pending;
            var same = receipt.AddedOn == torrent.AddedOn && receipt.Server == ArrActivityTracking.ServerKey(arr)
                && receipt.Instance == instance && receipt.Type == arr.Type.ToLowerInvariant()
                && ClientPathKey(receipt.ContentPath) == ClientPathKey(torrent.ContentPath) && ClientPathKey(receipt.SavePath) == ClientPathKey(torrent.SavePath);
            if (!same) return receipt.Files.Any(file => file.State == "pending") ? ImportRescueOutcome.Pending : null;
            if (receipt.State is "blocked" or "partial") return ImportRescueOutcome.NotApplicable;
            using var client = ArrImportClient.Create(arr);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var token = timeout.Token;
            var now = _rescueClock.GetUtcNow().ToUnixTimeSeconds();
            if (receipt.State == "pending" && now - receipt.CheckedAt < 60) return ImportRescueOutcome.Pending;
            var queue = await ReadImportQueueAsync(client, torrent.Hash, token);
            if (receipt.State == "confirmed")
            {
                if (queue.Count > 0) return ImportRescueOutcome.Pending;
                await ObserveAsync("confirmation", category, torrent.Hash, true, token);
                return ImportRescueOutcome.Confirmed;
            }
            var pending = receipt.Files.Where(file => file.State == "pending").ToArray();
            var since = DateTimeOffset.FromUnixTimeSeconds(pending.Min(file => file.SubmittedAt)).ToString("O");
            var history = await client.ReadImportAsync($"history/since?date={Uri.EscapeDataString(since)}&eventType={(receipt.Type is "lidarr" or "readarr" ? 8 : 3)}", token) as JArray
                ?? throw new IOException("Invalid import history.");
            var commands = await client.ReadImportAsync("command", token) as JArray ?? throw new IOException("Invalid import command evidence.");
            if (!await RescueTorrentUnchangedAsync(torrent, token, allowMissing)) return ImportRescueOutcome.Pending;
            foreach (var file in pending)
            {
                var operation = new ArrActivityOperation { Type = receipt.Type, DownloadId = receipt.Hash, SubmittedAt = file.SubmittedAt };
                if (file.MediaIds.All(id => history.OfType<JObject>().Any(item => ConfirmsImportFile(item, operation, file, id))))
                { file.State = "confirmed"; file.Reason = "Arr history confirmed this file's media import."; continue; }
                var command = commands.OfType<JObject>().FirstOrDefault(item => item["id"]?.Value<int?>() == file.CommandId);
                var status = command?["status"]?.Value<string>()?.ToLowerInvariant();
                if (command != null && now - file.SubmittedAt >= 900
                    && status is "completed" or "failed" or "aborted" or "cancelled" or "canceled")
                {
                    file.State = "failed";
                    file.Reason = "Arr did not confirm this file's import. " + DiagnosticText.Safe(command!["message"]?.Value<string>()
                        ?? command["exception"]?.Value<string>() ?? status);
                }
                // Missing command receipts are uncertain, even after the confirmation window; never replay them.
            }
            receipt.CheckedAt = now;
            SetPlanState(receipt);
            if (!await StoreImportPlanAsync(key, receipt, previous, token)) return ImportRescueOutcome.Pending;
            if (receipt.State != "pending") await RecordImportPlanAsync(torrent, receipt, receipt.State);
            if (receipt.State == "confirmed" && queue.Count == 0)
            {
                await ObserveAsync("confirmation", category, torrent.Hash, true, token);
                return ImportRescueOutcome.Confirmed;
            }
            if (receipt.Files.Any(file => file.State == "pending"))
            {
                if (pending.Any(file => now - file.SubmittedAt >= 900))
                    await ObserveAsync("confirmation", category, torrent.Hash, false, token, "An import submission remains uncertain; automatic replay and payload removal are held.");
                return ImportRescueOutcome.Pending;
            }
            return ImportRescueOutcome.NotApplicable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await ObserveAsync("confirmation", category, torrent.Hash, false, ct, DiagnosticText.Error(ex));
            return ImportRescueOutcome.Pending;
        }
    }

    private static bool ConfirmsImportFile(JObject item, ArrActivityOperation operation, ImportPlanFile file, int id)
    {
        if (!ArrActivityCollector.PositiveImport(item, operation)) return false;
        var dropped = item["data"]?["droppedPath"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(dropped) && ClientPathKey(dropped) != ClientPathKey(file.Path)) return false;
        return operation.Type switch
        {
            "radarr" => item["movieId"]?.Value<int?>() == id,
            "sonarr" => item["episodeId"]?.Value<int?>() == id,
            "readarr" => item["bookId"]?.Value<int?>() == id,
            // Lidarr history is album-scoped; an exact source path is required to confirm a track file.
            "lidarr" => !string.IsNullOrWhiteSpace(dropped) && item["albumId"]?.Value<int?>() == file.AlbumId,
            _ => false
        };
    }

    private async Task ReconcileMissingImportPlansAsync(string category, CancellationToken ct)
    {
        if (!_dbContext.Database.IsSqlite()) return;
        using var configuration = _config.BeginOperation();
        using var processing = ConfigMutationGuard.EnterProcessing();
        if (_config.Revision != _config.LatestRevision) return;
        var instance = CategoryOwnershipHelper.FindArrSectionForCategory(_config, category);
        if (instance == null || !_config.ArrInstances.TryGetValue(instance, out var arr)) return;
        var server = ArrActivityTracking.ServerKey(arr);
        var cursorKey = MaintenancePersistence.Key("", "", "cursor:import-plan:" + server + ":" + instance);
        var cursor = MaintenancePersistence.Read<ScanCursor>(_dbContext, cursorKey);
        var rows = await _dbContext.MaintenanceState.FromSqlInterpolated($"""
            SELECT Key, Data FROM torrentarr_maintenance_state
            WHERE substr(Key, -12) = '|import-plan'
                AND CASE WHEN json_valid(Data) THEN json_extract(Data, '$.State') END = 'pending'
                AND CASE WHEN json_valid(Data) THEN json_extract(Data, '$.Instance') END = {instance}
                AND CASE WHEN json_valid(Data) THEN json_extract(Data, '$.Server') END = {server}
            ORDER BY CASE WHEN Key > {cursor.Position} THEN 0 ELSE 1 END, Key LIMIT 100
            """).AsNoTracking().ToListAsync(ct);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        foreach (var row in rows)
        {
            if (budget.IsCancellationRequested || _config.Revision != _config.LatestRevision) break;
            cursor.Position = row.Key;
            try
            {
                var receipt = ImportPlanReceipt.Parse(row.Data);
                var torrent = new TorrentInfo
                {
                    Hash = receipt.Hash,
                    ClientInstanceId = receipt.Client,
                    AddedOn = receipt.AddedOn,
                    Category = category,
                    ContentPath = receipt.ContentPath,
                    SavePath = receipt.SavePath
                };
                var client = _qbitManager.GetClient(receipt.Client);
                if (client == null || (await client.GetTorrentsAsync(cancellationToken: budget.Token)).Any(item => item.Hash.Equals(receipt.Hash, StringComparison.OrdinalIgnoreCase))) continue;
                if (await ReconcileImportPlanAsync(torrent, category, budget.Token, allowMissing: true) == ImportRescueOutcome.Confirmed)
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE torrentlibrary SET imported=1, importsubmittedat=NULL
                        WHERE hash={receipt.Hash} COLLATE NOCASE AND qbitinstance={receipt.Client} COLLATE NOCASE
                            AND arrinstance={instance} COLLATE NOCASE AND torrentaddedon={receipt.AddedOn}
                        """, budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Import plan reconciliation held for {Instance}: {Error}", instance, DiagnosticText.Error(ex));
            }
        }
        if (rows.Count > 0 && _config.Revision == _config.LatestRevision) MaintenancePersistence.Write(_dbContext, cursorKey, cursor);
    }
}
