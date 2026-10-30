using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Services;

/// <summary>Records profile recovery intent before changing Arr; never overwrites an unrelated profile.</summary>
public class QualityProfileSwitcherService(ILogger<QualityProfileSwitcherService> logger, TorrentarrDbContext db,
    DatabaseRestartCoordinator restartCoordinator, IOperationalIssueReporter? issues = null)
{
    public Task ForceResetAllTempProfilesAsync(string instanceName, ArrInstanceConfig config, CancellationToken ct = default)
        => config.Search.ForceResetTempProfiles ? RunAsync(instanceName, config, null, null, ct) : Task.CompletedTask;

    public Task RestoreTimedOutProfilesAsync(string instanceName, ArrInstanceConfig config, CancellationToken ct = default)
        => config.Search.UseTempForMissing && config.Search.TempProfileResetTimeoutMinutes > 0
            ? RunAsync(instanceName, config, null, DateTime.UtcNow.AddMinutes(-config.Search.TempProfileResetTimeoutMinutes), ct) : Task.CompletedTask;

    public async Task<bool> SwitchToTempProfilesAsync(string instanceName, ArrInstanceConfig config,
        IEnumerable<SearchCandidate> candidates, CancellationToken ct = default)
    {
        if (!config.Search.UseTempForMissing || config.Search.QualityProfileMappings.Count == 0) return true;
        var missing = candidates.Where(c => string.Equals(c.Reason, "Missing", StringComparison.OrdinalIgnoreCase)).ToList();
        if (missing.Count == 0) return true;
        return await RunAsync(instanceName, config, missing, null, ct);
    }

    private async Task<bool> RunAsync(string instance, ArrInstanceConfig config, List<SearchCandidate>? candidates, DateTime? cutoff, CancellationToken ct)
    {
        var subject = candidates == null ? "restore" : "prepare";
        var ownerIds = candidates?.Select(c => config.Type.ToLowerInvariant() switch
        {
            "sonarr" => c.SeriesId,
            "lidarr" => c.ArtistId,
            "readarr" => c.AuthorId,
            _ => c.ArrId
        }).Where(id => id.HasValue).Select(id => id!.Value).Distinct().Order();
        var failureSubject = ownerIds == null ? "" : string.Join(",", ownerIds);
        try
        {
            switch (config.Type.ToLowerInvariant())
            {
                case "radarr":
                    var r = new RadarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
                    return await ProcessAsync<MoviesFilesModel>(instance, config, candidates?.Select(c => c.ArrId).ToHashSet(), cutoff,
                        async id => (await r.GetMovieAsync(id, ct))?.QualityProfileId,
                        id => r.GetQualityProfilesAsync(ct), (id, profile, expected) => r.UpdateMovieQualityProfileAsync(id, profile, ct, expected), ct);
                case "sonarr":
                    var s = new SonarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
                    return await ProcessAsync<SeriesFilesModel>(instance, config, candidates?.Where(c => c.SeriesId.HasValue).Select(c => c.SeriesId!.Value).ToHashSet(), cutoff,
                        async id => (await s.GetSeriesAsync(id, ct))?.QualityProfileId,
                        id => s.GetQualityProfilesAsync(ct), (id, profile, expected) => s.UpdateSeriesQualityProfileAsync(id, profile, ct, expected), ct);
                case "lidarr":
                    var l = new LidarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
                    return await ProcessAsync<ArtistFilesModel>(instance, config, candidates?.Where(c => c.ArtistId.HasValue).Select(c => c.ArtistId!.Value).ToHashSet(), cutoff,
                        async id => (await l.GetArtistAsync(id, ct))?.QualityProfileId,
                        id => l.GetQualityProfilesAsync(ct), (id, profile, expected) => l.UpdateArtistQualityProfileAsync(id, profile, ct, expected), ct);
                case "readarr":
                    var b = new ReadarrClient(config.URI, config.APIKey, config.SkipTLSVerify);
                    return await ProcessAsync<AuthorFilesModel>(instance, config, candidates?.Where(c => c.AuthorId.HasValue).Select(c => c.AuthorId!.Value).ToHashSet(), cutoff,
                        async id => (await b.GetAuthorAsync(id, ct))?.QualityProfileId,
                        id => b.GetQualityProfilesAsync(ct), (id, profile, expected) => b.UpdateAuthorQualityProfileAsync(id, profile, ct, expected), ct);
                default: return false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Profile {Operation} failed for {Instance}; recovery evidence retained", subject, instance);
            if (issues != null) await issues.ReportAsync(Issue(instance, subject, failureSubject), ct);
            return false;
        }
    }

    // All four catalog owners use the same mapped recovery columns and failure rules.
    private async Task<bool> ProcessAsync<T>(string instance, ArrInstanceConfig config, HashSet<int>? ids, DateTime? cutoff,
        Func<int, Task<int?>> read, Func<int, Task<List<QualityProfile>>> profiles, Func<int, int, int, Task<bool>> update, CancellationToken ct) where T : class
    {
        var query = db.Set<T>().Where(e => EF.Property<string>(e, "ArrInstance") == instance);
        if (ids != null) query = query.Where(e => ids.Contains(EF.Property<int>(e, "ArrId")));
        else query = query.Where(e => EF.Property<int?>(e, "OriginalProfileId") != null);
        if (cutoff != null) query = query.Where(e => EF.Property<DateTime?>(e, "LastProfileSwitchTime") < cutoff);
        var rows = await query.ToListAsync(ct);
        if (ids != null && rows.Count != ids.Count) throw new InvalidOperationException("Profile owner is absent from the catalog.");
        List<QualityProfile>? available = null;
        var ok = true;
        foreach (var row in rows)
        {
            var entry = db.Entry(row);
            var id = entry.Property<int>("ArrId").CurrentValue;
            var original = entry.Property<int?>("OriginalProfileId");
            var temporary = entry.Property<int?>("CurrentProfileId");
            var switchedAt = entry.Property<DateTime?>("LastProfileSwitchTime");
            var issue = Issue(instance, ids == null ? "restore" : "prepare", id.ToString());
            try
            {
                var observed = await read(id) ?? throw new IOException("Current Arr profile is unknown.");
                if (original.CurrentValue.HasValue)
                {
                    if (observed != original.CurrentValue && observed != temporary.CurrentValue)
                        throw new InvalidOperationException("Arr profile changed outside Torrentarr; manual reconciliation required.");
                    if (ids == null)
                    {
                        if (observed != original.CurrentValue && !await RetryAsync(() => update(id, original.CurrentValue.Value, observed), config, ct))
                            throw new IOException("Arr rejected profile restoration.");
                        temporary.CurrentValue = original.CurrentValue;
                        original.CurrentValue = null;
                        switchedAt.CurrentValue = null;
                        await db.SaveChangesWithRetryAsync(logger, restartCoordinator, cancellationToken: ct);
                        if (issues != null) await issues.ResolveAsync(Issue(instance, "prepare", id.ToString()), ct);
                    }
                    else if (observed == original.CurrentValue)
                    {
                        if (!temporary.CurrentValue.HasValue || !await RetryAsync(() => update(id, temporary.CurrentValue.Value, observed), config, ct))
                            throw new IOException("Arr rejected temporary profile preparation.");
                    }
                }
                else if (ids != null)
                {
                    available ??= await profiles(id);
                    var currentName = available.FirstOrDefault(p => p.Id == observed)?.Name
                        ?? throw new IOException("Current quality profile is absent from Arr profiles.");
                    if (!config.Search.QualityProfileMappings.TryGetValue(currentName, out var targetName)) continue;
                    var target = available.FirstOrDefault(p => p.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new IOException("Configured temporary quality profile is absent.");
                    if (target.Id == observed) continue;
                    original.CurrentValue = observed;
                    temporary.CurrentValue = target.Id;
                    switchedAt.CurrentValue = DateTime.UtcNow;
                    // Commit intent first. A crash or uncertain PUT must never lose the original profile.
                    await db.SaveChangesWithRetryAsync(logger, restartCoordinator, cancellationToken: ct);
                    if (!await RetryAsync(() => update(id, target.Id, observed), config, ct)) throw new IOException("Arr rejected temporary profile preparation.");
                }
                if (issues != null) await issues.ResolveAsync(issue, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                ok = false;
                // Discard uncommitted local changes, including a failed restoration save.
                entry.State = EntityState.Detached;
                logger.LogWarning(ex, "Profile operation failed for {Instance}/{Id}; search skipped and evidence preserved", instance, id);
                if (issues != null) await issues.ReportAsync(issue, ct);
            }
        }
        if (ok && issues != null) await issues.ResolveAsync(Issue(instance, ids == null ? "restore" : "prepare", ids == null ? "" : string.Join(",", ids.Order())), ct);
        return ok;
    }

    private static OperationalIssueReport Issue(string instance, string operation, string id = "") => new(
        "profile." + operation, "automation", operation == "restore" ? "Quality profile could not be restored" : "Search skipped: quality profile preparation failed",
        "The affected search is held back. Saved original profiles are preserved; an unrelated profile will not be overwritten.",
        "Check Arr connectivity, profile mappings and database health. Reconcile manual profile changes before retrying.", ArrInstance: instance, SubjectType: "profile", SubjectId: id, Target: "config");

    private async Task<bool> RetryAsync(Func<Task<bool>> action, ArrInstanceConfig config, CancellationToken ct)
    {
        var attempts = Math.Max(1, config.Search.ProfileSwitchRetryAttempts);
        for (var i = 0; i < attempts; i++)
        {
            try { if (await action()) return true; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Profile request failed (attempt {Attempt})", i + 1); }
            if (i + 1 < attempts) await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return false;
    }
}
