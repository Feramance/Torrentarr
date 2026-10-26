using Microsoft.EntityFrameworkCore;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

public sealed class RecoveryIncident
{
    public string TorrentName { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset? RequestExpiresAt { get; set; }
    public string Stage { get; set; } = "new";
    public DateTimeOffset? RequestedAt { get; set; }
    public DateTimeOffset? CheckingAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? FailureSince { get; set; }
    public DateTimeOffset? LastObserved { get; set; }
    public DateTimeOffset? HealthySince { get; set; }
    public int Attempts { get; set; }
    public long AddedOn { get; set; }
}

public static class MaintenanceRecovery
{
    public static bool IsError(TorrentInfo torrent, MaintenanceConfig cfg) => cfg.ErrorRecovery.States.Any(s => torrent.State.Equals(s, StringComparison.OrdinalIgnoreCase));
    public static bool HealthyTracker(IEnumerable<TorrentTracker> trackers) => trackers.Any(t => t.Status == 2 && string.IsNullOrWhiteSpace(t.Msg));
    public static RecoveryIncident Read(TorrentarrDbContext db, TorrentInfo torrent, string kind)
    {
        var state = MaintenancePersistence.Read<RecoveryIncident>(db, MaintenancePersistence.Key(torrent.ClientInstanceId, torrent.Hash, "recovery:" + kind));
        return state.AddedOn != 0 && state.AddedOn != torrent.AddedOn ? new() : state;
    }

    public static void Observe(TorrentarrDbContext db, TorrentInfo torrent, string kind, bool error, ErrorRecoveryConfig cfg, DateTimeOffset now)
    {
        using var transaction = db.Database.BeginTransaction();
        var state = Read(db, torrent, kind);
        if (state.AddedOn != 0 && state.AddedOn != torrent.AddedOn) state = new();
        state.AddedOn = torrent.AddedOn;
        state.TorrentName = torrent.Name;
        var contiguous = state.LastObserved is { } last && now >= last && now - last <= TimeSpan.FromMinutes(6);
        if (state.RequestedAt.HasValue && state.CompletedAt == null && now - state.RequestedAt > TimeSpan.FromMinutes(cfg.RecheckTimeoutMinutes))
            state.Stage = "unconfirmed";
        if (state.Stage != "unconfirmed" && torrent.State.Contains("checking", StringComparison.OrdinalIgnoreCase) && state.RequestedAt.HasValue)
        { state.CheckingAt ??= now; state.Stage = "checking"; }
        else if (state.Stage != "unconfirmed" && state.CheckingAt.HasValue && state.CompletedAt == null)
        { state.CompletedAt = now; state.Stage = "checked"; }
        if (state.RequestedAt.HasValue && state.CompletedAt == null && now - state.RequestedAt > TimeSpan.FromMinutes(cfg.RecheckTimeoutMinutes))
            state.Stage = "unconfirmed";
        if (error)
        {
            state.HealthySince = null;
            if (state.CompletedAt.HasValue)
            { if (!contiguous) state.FailureSince = now; state.FailureSince ??= now; state.Stage = "recurring"; }
        }
        else if (state.Stage is not ("checking" or "requested") && !torrent.State.Contains("checking", StringComparison.OrdinalIgnoreCase))
        {
            state.FailureSince = null;
            if (!contiguous) state.HealthySince = now;
            state.HealthySince ??= now;
            if (now - state.HealthySince >= TimeSpan.FromMinutes(cfg.HealthyResetMinutes)) state = new();
        }
        state.LastObserved = now;
        MaintenancePersistence.Write(db, MaintenancePersistence.Key(torrent.ClientInstanceId, torrent.Hash, "recovery:" + kind), state);
        transaction.Commit();
    }

    public static bool CanRecycle(RecoveryIncident state, ErrorRecoveryConfig cfg, DateTimeOffset now)
        => state.Stage == "recurring" && state.CompletedAt.HasValue && state.FailureSince.HasValue
            && now - state.FailureSince >= TimeSpan.FromMinutes(cfg.SustainedFailureMinutes)
            && state.LastObserved.HasValue && now - state.LastObserved <= TimeSpan.FromMinutes(6);

    public static async Task<bool> RequestAsync(TorrentarrDbContext db, ITorrentClient client, TorrentInfo torrent, string kind, ErrorRecoveryConfig cfg, CancellationToken ct)
    {
        var key = MaintenancePersistence.Key(torrent.ClientInstanceId, torrent.Hash, "recovery:" + kind);
        if ((await client.GetTorrentsAsync(cancellationToken: ct)).Any(t => t.State.Contains("checking", StringComparison.OrdinalIgnoreCase))) return false;
        using (var transaction = db.Database.BeginTransaction())
        {
            var state = Read(db, torrent, kind);
            var active = db.MaintenanceState.AsNoTracking().Where(s => s.Key.Contains("|recovery:") && s.Key != key).ToList()
                .Select(s => Newtonsoft.Json.JsonConvert.DeserializeObject<RecoveryIncident>(s.Data))
                .Any(s => s is { Stage: "requested" or "checking", CompletedAt: null, RequestedAt: not null }
                    && DateTimeOffset.UtcNow < (s.RequestExpiresAt ?? s.RequestedAt.Value.AddHours(1)));
            if (active || state.Stage is "requested" or "checking" or "unconfirmed" || !client.Capabilities.Recheck || state.Attempts >= cfg.MaxRepairAttempts
                || state.RequestedAt is { } previous && DateTimeOffset.UtcNow - previous < TimeSpan.FromMinutes(cfg.RetryMinutes)) return false;
            // A durable claim prevents workers and maintenance from submitting the same check.
            state.AddedOn = torrent.AddedOn;
            state.CheckingAt = null; state.CompletedAt = null; state.FailureSince = null; state.HealthySince = null;
            state.Attempts++; state.RequestedAt = DateTimeOffset.UtcNow; state.RequestExpiresAt = DateTimeOffset.UtcNow.AddMinutes(cfg.RecheckTimeoutMinutes); state.TorrentName = torrent.Name; state.Stage = "requested";
            MaintenancePersistence.Write(db, key, state); transaction.Commit();
        }
        var accepted = await client.RecheckTorrentsAsync([torrent.Hash], ct);
        if (!accepted)
        {
            using var transaction = db.Database.BeginTransaction();
            var state = Read(db, torrent, kind); state.Attempts--; state.Stage = "rejected";
            MaintenancePersistence.Write(db, key, state); transaction.Commit();
        }
        return accepted;
    }

    public static bool IsErrorState(TorrentInfo torrent, ErrorRecoveryConfig config) => torrent.State.Equals("error", StringComparison.OrdinalIgnoreCase)
        || torrent.State.Equals("missingFiles", StringComparison.OrdinalIgnoreCase) || config.States.Contains(torrent.State, StringComparer.OrdinalIgnoreCase);

    public static bool BlocksAutomaticRemoval(TorrentarrDbContext db, TorrentInfo torrent, ErrorRecoveryConfig config)
    {
        if (IsErrorState(torrent, config)) return true;
        var incident = Read(db, torrent, "ErrorRecovery");
        return incident.Attempts > 0 && incident.Stage != "new";
    }

    public static async Task<bool> ProcessAsync(TorrentarrDbContext db, ITorrentClient client, TorrentInfo torrent,
        RecoveryConfig config, int minimumAgeSeconds, bool withinBudget, CancellationToken ct)
    {
        var error = IsErrorState(torrent, config);
        if (!error && Read(db, torrent, "ErrorRecovery").Stage == "new") return false;
        Observe(db, torrent, "ErrorRecovery", error, config, DateTimeOffset.UtcNow);
        var state = Read(db, torrent, "ErrorRecovery");
        if (!error) return false;
        var tags = torrent.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var eligible = config.Enabled && config.States.Contains(torrent.State, StringComparer.OrdinalIgnoreCase)
            && (config.IncludedCategories.Count == 0 || config.IncludedCategories.Contains(torrent.Category, StringComparer.OrdinalIgnoreCase))
            && !config.ExcludedCategories.Contains(torrent.Category, StringComparer.OrdinalIgnoreCase)
            && !config.ExcludedTags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()
            && torrent.AddedOn > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - torrent.AddedOn >= Math.Max(minimumAgeSeconds, config.MinimumTorrentAgeMinutes * 60L);
        state.Reason = eligible ? "Repair is pending or exhausted; torrent is kept." : "Automatic repair is disabled, excluded, or waiting for its age grace; torrent is kept.";
        MaintenancePersistence.Write(db, MaintenancePersistence.Key(torrent.ClientInstanceId, torrent.Hash, "recovery:ErrorRecovery"), state);
        if (!eligible || !withinBudget || config.MaxPerRun == 0) return false;
        if (state.Stage is not ("new" or "rejected" or "checked" or "recurring") || state.Attempts >= config.MaxRepairAttempts) return false;
        var previousRequest = state.RequestedAt;
        await RequestAsync(db, client, torrent, "ErrorRecovery", config, ct);
        return Read(db, torrent, "ErrorRecovery").RequestedAt != previousRequest;
    }

    public static async Task<bool> RequestExplicitAsync(TorrentarrDbContext db, ITorrentClient client, TorrentInfo torrent, CancellationToken ct)
    {
        var config = new ErrorRecoveryConfig();
        return await RequestAsync(db, client, torrent, "ExplicitRecheck", config, ct);
    }

    public static void ObserveExplicitExits(TorrentarrDbContext db, string client, IReadOnlyList<TorrentInfo> torrents, string recheckCategory)
    {
        var prefix = client.ToUpperInvariant() + "|";
        var active = torrents.Where(t => t.Category.Equals(recheckCategory, StringComparison.OrdinalIgnoreCase)).Select(t => t.Hash.ToUpperInvariant()).ToHashSet();
        var records = db.MaintenanceState.Where(s => s.Key.StartsWith(prefix) && s.Key.EndsWith("|recovery:ExplicitRecheck")).ToList();
        foreach (var record in records.Where(r => !active.Contains(r.Key.Split('|')[1]))) db.MaintenanceState.Remove(record);
        db.SaveChanges();
    }
}
