using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RestSharp;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Services;

/// <summary>Worker identity follows async calls, including calls through shared clients.</summary>
public static class WorkerHistory
{
    private sealed record Context(IServiceScopeFactory Scopes, ILogger Logger, string Instance, string Loop, ArrInstanceConfig Config);
    private static readonly AsyncLocal<Context?> Current = new();
    private sealed record Subject(string Type, string Id, string Title, string Reason, string RunId);
    private static readonly AsyncLocal<Subject?> CurrentSubject = new();
    internal static IDisposable ForSubject(string type, string id, string title, string reason = "", string runId = "")
    {
        var previous = CurrentSubject.Value;
        CurrentSubject.Value = new(type, id, SafeText(title), SafeText(reason), runId);
        return new Release(() => CurrentSubject.Value = previous);
    }
    private static readonly AsyncLocal<bool> Catalog = new();
    private static readonly AsyncLocal<string?> ProfileOperation = new();
    internal static IDisposable ForProfileOperation(string operation)
    {
        var previous = ProfileOperation.Value;
        ProfileOperation.Value = operation;
        return new Release(() => ProfileOperation.Value = previous);
    }
    private static readonly AsyncLocal<(int? Before, int After)?> Profile = new();
    internal static IDisposable ProfileChange(int? before, int after)
    {
        var previous = Profile.Value;
        Profile.Value = (before, after);
        return new Release(() => Profile.Value = previous);
    }
    internal static IDisposable ObserveCatalog()
    {
        var previous = Catalog.Value;
        Catalog.Value = true;
        return new Release(() => Catalog.Value = previous);
    }
    private sealed record Activity(string Summary, DateTimeOffset At);
    private sealed record Progress(Activity? Current, Activity? Last);
    private static readonly ConcurrentDictionary<(string, string), Progress> ProgressByLoop = new();
    public static IDisposable Begin(IServiceScopeFactory scopes, ILogger logger, string instance, string loop, ArrInstanceConfig config)
    {
        var previous = Current.Value;
        Current.Value = new(scopes, logger, instance, loop, config);
        return new Release(() => Current.Value = previous);
    }
    private sealed class Release(Action release) : IDisposable { public void Dispose() => release(); }

    public static WorkerLoopProgress Describe(string instance, string loop, WorkerLoopProgress progress)
    {
        var action = ProgressByLoop.GetValueOrDefault((instance, loop));
        return progress with
        {
            CurrentAction = action?.Current?.Summary,
            CurrentActionAt = action?.Current?.At,
            LastAction = action?.Last?.Summary,
            LastActionAt = action?.Last?.At
        };
    }

    public static async Task<Dictionary<(string, string), WorkerHistoryEvent>> LastActionsAsync(TorrentarrDbContext db,
        IEnumerable<string> instances, CancellationToken ct = default)
    {
        var result = new Dictionary<(string, string), WorkerHistoryEvent>();
        foreach (var instance in instances)
            foreach (var loop in new[] { "search", "torrent" })
            {
                var entry = await db.WorkerHistory.AsNoTracking().Where(e => e.Instance == instance && e.Loop == loop)
                    .OrderByDescending(e => e.Id).FirstOrDefaultAsync(ct);
                if (entry != null) result[(instance, loop)] = entry;
            }
        return result;
    }

    public static WorkerLoopProgress? RestoreLast(WorkerLoopProgress? progress, bool alive, string instance, string loop,
        IReadOnlyDictionary<(string, string), WorkerHistoryEvent> last)
    {
        var entry = last.GetValueOrDefault((instance, loop));
        if (progress == null && entry == null) return null;
        progress ??= new(alive ? "Running" : "Stopped", DateTimeOffset.UtcNow);
        if (entry != null && (progress.LastActionAt == null || entry.Timestamp > progress.LastActionAt))
            progress = progress with { LastAction = entry.Summary, LastActionAt = entry.Timestamp };
        var active = alive && !progress.IsStalled(DateTimeOffset.UtcNow);
        return progress with
        {
            CurrentAction = active ? progress.CurrentAction : null,
            CurrentActionAt = active ? progress.CurrentActionAt : null,
            LastAction = progress.LastAction ?? entry?.Summary,
            LastActionAt = progress.LastActionAt ?? entry?.Timestamp
        };
    }

    internal static bool IsRecording => Current.Value != null;
    internal static IDisposable Active(string summary)
    {
        if (Current.Value is not { } context) return new Release(() => { });
        var key = (context.Instance, context.Loop);
        var activity = new Activity(DiagnosticText.Safe(summary), DateTimeOffset.UtcNow);
        var previous = ProgressByLoop.GetValueOrDefault(key)?.Current;
        ProgressByLoop.AddOrUpdate(key, new Progress(activity, null), (_, value) => value with { Current = activity });
        return new Release(() => ProgressByLoop.AddOrUpdate(key, new Progress(previous, activity),
            (_, value) => value with { Current = previous, Last = value.Last ?? activity }));
    }

    internal static Task RecordAsync(WorkerHistoryEvent entry) => RecordBatchAsync([entry]);

    internal static async Task RecordBatchAsync(IReadOnlyList<WorkerHistoryEvent> entries)
    {
        if (Current.Value is not { } context || entries.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in entries)
        {
            entry.Instance = context.Instance;
            entry.Loop = context.Loop;
            entry.Server = ArrActivityTracking.ServerKey(context.Config);
            entry.Timestamp = now;
            entry.Summary = SafeText(entry.Summary);
            entry.SubjectId = SafeText(entry.SubjectId);
        }
        try
        {
            using var scope = context.Scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
            db.Database.SetCommandTimeout(1);
            foreach (var batch in entries.Chunk(500))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                db.WorkerHistory.AddRange(batch);
                await db.SaveChangesAsync(timeout.Token);
                db.ChangeTracker.Clear();
            }
            var activity = new Activity(entries[^1].Summary, now);
            ProgressByLoop.AddOrUpdate((context.Instance, context.Loop), new Progress(null, activity),
                (_, value) => value with { Last = activity });
        }
        catch (Exception ex)
        {
            // An audit failure must never turn an accepted mutation into a retry.
            context.Logger.LogWarning("Worker history unavailable for {Instance}: {ErrorType}", context.Instance, ex.GetType().Name);
        }
    }

    private static string SafeText(string value)
    {
        var safe = DiagnosticText.Safe(value);
        return safe[..Math.Min(500, safe.Length)];
    }

    internal sealed record RequestInfo(string Type, string SubjectType, string SubjectId, string Summary, string Details);
    internal static RequestInfo? DescribeRequest(RestRequest request, bool torrent = false)
    {
        if (!IsRecording || request.Method is Method.Get or Method.Head or Method.Options) return null;
        var parts = request.Resource.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (torrent)
        {
            if (!parts.Contains("torrents") || parts.Length == 0) return null;
            var operation = parts[^1];
            var type = operation switch
            {
                "delete" => "torrent-removal",
                "add" => "torrent-add",
                "pause" or "stop" => "pause",
                "resume" or "start" => "resume",
                "addTags" or "removeTags" or "createTags" => "tags",
                "addTrackers" or "removeTrackers" => "trackers",
                "setCategory" => "category",
                "recheck" => "recheck",
                _ => "torrent-update"
            };
            var hash = request.Parameters.FirstOrDefault(p => p.Name is "hash" or "hashes")?.Value?.ToString() ?? "";
            var metadata = new Dictionary<string, string>();
            foreach (var parameter in request.Parameters.Where(p => p.Name is "deleteFiles" or "category" or "tags" or "priority" or "ratioLimit" or "seedingTimeLimit" or "inactiveSeedingTimeLimit" or "limit" or "value" or "enable"))
                metadata[parameter.Name!] = SafeText(parameter.Value?.ToString() ?? "");
            return new(type, "torrent", hash, $"{operation} · {SafeText(hash)}", JsonSerializer.Serialize(metadata));
        }
        if (parts.Length < 3) return null;
        var subject = parts[2];
        if (subject is "history" or "system") return null;
        JObject? body = null;
        try
        {
            var value = request.Parameters.FirstOrDefault(p => p.Type == ParameterType.RequestBody)?.Value;
            if (value != null) body = value is string text ? JObject.Parse(text) : JObject.FromObject(value);
        }
        catch (Exception) { /* Only optional, allowlisted metadata is used. */ }
        var command = body?["name"]?.Value<string>() ?? "";
        var action = subject == "command" ? command.Contains("Search", StringComparison.OrdinalIgnoreCase) ? "search"
                : command.Contains("Import", StringComparison.OrdinalIgnoreCase) || command.Contains("Downloaded", StringComparison.OrdinalIgnoreCase) ? "import" : "command"
            : subject == "queue" ? "queue-removal"
            : subject == "manualimport" ? "import"
            : subject is "movie" or "series" or "episode" or "artist" or "album" or "track" or "author" or "book"
                ? request.Method == Method.Delete ? "delete" : request.Method == Method.Post ? "add" : "update" : "command";
        var id = parts.Length > 3 ? parts[3] : "";
        var details = new JObject();
        foreach (var field in new[] { "movieIds", "episodeIds", "seriesId", "artistId", "albumIds", "authorId", "bookIds", "qualityProfileId", "downloadClientId", "downloadId", "importMode" })
            if (body?[field] is { } token) details[field] = token is JArray array ? new JArray(array.Take(100)) : token.DeepClone();
        foreach (var parameter in request.Parameters.Where(p => p.Name is "blocklist" or "removeFromClient" or "skipRedownload"))
            details[parameter.Name!] = parameter.Value?.ToString();
        if (Profile.Value is { } profile)
        {
            action = "temp-profile";
            details["operation"] = ProfileOperation.Value ?? "change";
            details["previousProfileId"] = profile.Before;
            details["qualityProfileId"] = profile.After;
        }
        var label = subject == "command" ? command : $"{request.Method} {subject} {id}";
        if (CurrentSubject.Value is { } subjectInfo)
        {
            label += " · " + subjectInfo.Title;
            details["reason"] = subjectInfo.Reason;
        }
        if (Profile.Value is { } target) label = $"Profile {ProfileOperation.Value ?? "change"} · {subject} {id} → {target.After}";
        return new(action, subject, id, label, details.ToString(Newtonsoft.Json.Formatting.None));
    }

    internal static async Task<RestResponse> ExecuteAsync(RestRequest request, Func<Task<RestResponse>> execute, string? client = null)
    {
        var info = DescribeRequest(request, torrent: client != null);
        using var action = info == null ? null : Active(info.Summary);
        try
        {
            var response = await execute();
            await RecordResponseAsync(info, response, client ?? "");
            return response;
        }
        catch (Exception)
        {
            if (info != null) await RecordAsync(new()
            {
                Type = info.Type,
                SubjectType = CurrentSubject.Value?.Type ?? info.SubjectType,
                SubjectId = CurrentSubject.Value?.Id ?? info.SubjectId,
                RunId = CurrentSubject.Value?.RunId ?? "",
                Summary = info.Summary,
                Details = info.Details,
                Outcome = "unknown",
                ClientInstance = client ?? ArrActivityTracking.ClientInstance
            });
            throw;
        }
    }

    internal static async Task RecordResponseAsync(RequestInfo? info, RestResponse response, string client = "")
    {
        if (info == null) return;
        int? commandId = null;
        if (info.SubjectType == "command" && response.IsSuccessful)
        {
            try { commandId = JObject.Parse(response.Content ?? "{}")["id"]?.Value<int>(); }
            catch (Exception) { /* A missing receipt is an uncertain result, not completion. */ }
            if (commandId is <= 0) commandId = null;
        }
        var outcome = response.IsSuccessful ? info.SubjectType == "command" ? commandId.HasValue ? "submitted" : "unknown" : "completed"
            : response.ResponseStatus != ResponseStatus.Completed || (int)response.StatusCode == 0 ? "unknown" : "failed";
        if (info.Type == "torrent-add" && response.IsSuccessful && response.Content != "Ok.") outcome = "failed";
        var details = JObject.Parse(info.Details);
        details["httpStatus"] = (int)response.StatusCode;
        if (outcome is "failed" or "unknown") details["responseStatus"] = response.ResponseStatus.ToString();
        await RecordAsync(new()
        {
            Type = info.Type,
            SubjectType = CurrentSubject.Value?.Type ?? info.SubjectType,
            SubjectId = CurrentSubject.Value?.Id ?? info.SubjectId,
            RunId = CurrentSubject.Value?.RunId ?? "",
            Summary = info.Summary,
            Details = details.ToString(Newtonsoft.Json.Formatting.None),
            Outcome = outcome,
            ClientInstance = client.Length > 0 ? client : ArrActivityTracking.ClientInstance,
            CommandId = commandId,
            SubmittedAt = commandId.HasValue ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : null,
            CompletedAt = outcome == "completed" ? DateTimeOffset.UtcNow : null
        });
    }

    private static object? BoundedValue(object? value) => value is string text ? SafeText(text) : value;

    internal static List<WorkerHistoryEvent> CatalogChanges(TorrentarrDbContext db)
    {
        if (!IsRecording || !Catalog.Value) return [];
        var types = new Dictionary<Type, string>
        {
            [typeof(MoviesFilesModel)] = "movie",
            [typeof(SeriesFilesModel)] = "series",
            [typeof(EpisodeFilesModel)] = "episode",
            [typeof(ArtistFilesModel)] = "artist",
            [typeof(AlbumFilesModel)] = "album",
            [typeof(TrackFilesModel)] = "track",
            [typeof(AuthorFilesModel)] = "author",
            [typeof(BookFilesModel)] = "book"
        };
        string[] fields = ["Title", "SeriesTitle", "Name", "Monitored", "Year", "HasFile", "MovieFileId", "EpisodeFileId", "AlbumFileId", "TrackFileId", "BookFileId", "TrackNumber", "Duration", "BookCount", "ArtistTitle", "AuthorTitle", "ForeignAlbumId", "ForeignBookId", "QualityProfileId", "CustomFormatScore", "AirDateUtc", "ReleaseDate", "InCinemas", "DigitalRelease", "PhysicalRelease", "MinimumAvailability", "SeasonNumber", "EpisodeNumber"];
        var events = new List<WorkerHistoryEvent>();
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (!types.TryGetValue(entry.Metadata.ClrType, out var type)) continue;
            var instance = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "ArrInstance")?.CurrentValue?.ToString();
            if (instance != Current.Value!.Instance) continue;
            var changes = entry.Properties.Where(p => fields.Contains(p.Metadata.Name) && (entry.State != EntityState.Modified || !Equals(p.OriginalValue, p.CurrentValue)))
                .ToDictionary(p => p.Metadata.Name, p => new { before = entry.State == EntityState.Added ? null : BoundedValue(p.OriginalValue), after = entry.State == EntityState.Deleted ? null : BoundedValue(p.CurrentValue) });
            if (entry.State == EntityState.Modified && changes.Count == 0) continue;
            var action = entry.State == EntityState.Added ? "add" : entry.State == EntityState.Deleted ? "delete" : "update";
            var id = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "ArrId")?.CurrentValue?.ToString() ?? "";
            var title = entry.Properties.FirstOrDefault(p => p.Metadata.Name is "Title" or "SeriesTitle" or "Name")?.CurrentValue?.ToString() ?? id;
            events.Add(new()
            {
                Type = action,
                Source = "observed",
                SubjectType = type,
                SubjectId = id,
                Summary = $"{type} {action}: {DiagnosticText.Safe(title)}",
                Outcome = "completed",
                Details = JsonSerializer.Serialize(changes)
            });
        }
        return events;
    }
}
