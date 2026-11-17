using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Services;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.Endpoints;

public static class OperationalIssueEndpoints
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(30);

    private static IReadOnlyList<PendingOperationalIssue> Pending(IOperationalIssueReporter reporter, WorkerProcessSupervisor? workers)
        => reporter.Pending.Concat(workers?.GetPendingIssues() ?? []).GroupBy(p => new { p.Report.Code, Arr = p.Report.ArrInstance.ToUpperInvariant(), Client = p.Report.ClientInstance.ToUpperInvariant(), p.Report.SubjectType, p.Report.SubjectId }).Select(g => g.MaxBy(p => p.ObservedAt)!).ToList();

    private static DateTime VisibilityCutoff(IServiceProvider services)
        => (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime - GracePeriod;

    private static bool IsVisible(PendingOperationalIssue issue, DateTime cutoff)
        => !issue.Resolved && (issue.FirstObserved ?? issue.ObservedAt) <= cutoff
            && issue.Report.Code is not ("thumbnail.fetch" or "config.migration");

    private static IQueryable<OperationalIssue> VisibleActive(IQueryable<OperationalIssue> query,
        DateTime cutoff, IReadOnlyList<PendingOperationalIssue> pending)
    {
        query = query.Where(i => i.ResolvedAt == null && i.FirstObserved <= cutoff
            && i.Code != "thumbnail.fetch" && i.Code != "config.migration");
        foreach (var update in pending.Where(p => p.Resolved))
        {
            var report = update.Report;
            var through = update.ObservedAt;
            var arr = report.ArrInstance.ToUpperInvariant();
            var client = report.ClientInstance.ToUpperInvariant();
            query = query.Where(i => i.LastObserved > through || i.Code != report.Code || i.ArrInstance.ToUpper() != arr
                || i.ClientInstance.ToUpper() != client || i.SubjectType != report.SubjectType || i.SubjectId != report.SubjectId);
        }
        return query;
    }

    private static async Task DescribeSubjectsAsync(List<OperationalIssue> items, IServiceProvider services, CancellationToken ct)
    {
        var cache = services.GetService(typeof(ITorrentCacheService)) as ITorrentCacheService;
        var registry = services.GetService(typeof(ITorrentClientRegistry)) as ITorrentClientRegistry;
        var db = services.GetRequiredService<TorrentarrDbContext>();
        var config = services.GetRequiredService<TorrentarrConfig>();
        var clients = new Dictionary<long, string>();
        foreach (var item in items.Where(i => i.SubjectType == "torrent"))
        {
            var client = item.ClientInstance;
            if (client.Length == 0 && item.ArrInstance.Length > 0)
            {
                var hash = item.SubjectId.ToUpperInvariant();
                var arr = item.ArrInstance.ToUpperInvariant();
                var matches = await db.TorrentLibrary.AsNoTracking()
                    .Where(t => t.Hash.ToUpper() == hash && t.ArrInstance.ToUpper() == arr)
                    .Select(t => t.QbitInstance).Distinct().ToListAsync(ct);
                if (matches.Count == 1) client = matches[0];
            }
            clients[item.Id] = client;
        }
        foreach (var group in items.Where(i => i.SubjectType == "torrent").GroupBy(i => clients[i.Id]))
        {
            foreach (var item in group)
            {
                item.SubjectName = cache?.GetName(group.Key, item.SubjectId) is { } name ? DiagnosticText.Safe(name) : null;
                item.SubjectCategory = cache?.GetCategory(group.Key, item.SubjectId) is { } category ? DiagnosticText.Safe(category) : null;
            }
            if (group.Key.Length > 0 && group.Any(i => i.SubjectName == null || i.SubjectCategory == null) && registry?.GetClient(group.Key) is { } client)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    var torrents = await client.GetTorrentsAsync(cancellationToken: timeout.Token);
                    foreach (var item in group)
                    {
                        var torrent = torrents.Find(t => string.Equals(t.Hash, item.SubjectId, StringComparison.OrdinalIgnoreCase));
                        if (torrent == null) continue;
                        item.SubjectName = DiagnosticText.Safe(torrent.Name);
                        item.SubjectCategory = DiagnosticText.Safe(torrent.Category);
                        cache?.SetName(group.Key, item.SubjectId, item.SubjectName);
                        cache?.SetCategory(group.Key, item.SubjectId, item.SubjectCategory);
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested) { /* Issue history remains available when client metadata is unavailable. */ }
            }
        }
        foreach (var item in items.Where(i => i.SubjectType == "torrent" && i.SubjectCategory == null))
            if (config.ArrInstances.TryGetValue(item.ArrInstance, out var arr))
                item.SubjectCategory = DiagnosticText.Safe(arr.Category);
    }

    public static void MapOperationalIssueEndpoints(this WebApplication app, string prefix)
    {
        app.MapGet(prefix + "/issues/summary", async (TorrentarrDbContext db, IOperationalIssueReporter reporter, IServiceProvider services, CancellationToken ct) =>
        {
            var updates = Pending(reporter, services.GetService<WorkerProcessSupervisor>());
            var cutoff = VisibilityCutoff(services);
            var pending = updates.Where(p => IsVisible(p, cutoff)).ToList();
            try
            {
                var counts = await VisibleActive(db.OperationalIssues.AsNoTracking(), cutoff, updates)
                    .GroupBy(i => new { i.Category, i.ArrInstance, i.ClientInstance, i.Code })
                    .Select(g => new { g.Key.Category, severity = g.Any(i => i.Severity == "error") ? "error" : "warning", g.Key.ArrInstance, g.Key.ClientInstance, g.Key.Code, affected = g.Count() }).ToListAsync(ct);
                var instances = await db.OperationalIssues.AsNoTracking().Select(i => i.ArrInstance)
                    .Concat(db.OperationalIssues.AsNoTracking().Select(i => i.ClientInstance)).Where(i => i != "").Distinct().OrderBy(i => i).ToListAsync(ct);
                return Results.Ok(new { available = true, activeGroups = counts.Count, counts, instances, pending });
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return Results.Ok(new { available = false, activeGroups = (int?)null, counts = Array.Empty<object>(), pending }); }
        });
        app.MapGet(prefix + "/issues", async (TorrentarrDbContext db, IOperationalIssueReporter reporter, IServiceProvider services,
            string? category, string? state, string? severity, string? instance, string? q, int? page, int? page_size, CancellationToken ct) =>
        {
            if (category is not (null or "automation" or "system") || state is not (null or "active" or "resolved") || severity is not (null or "warning" or "error"))
                return Results.BadRequest(new { error = "Invalid issue filter." });
            var updates = Pending(reporter, services.GetService<WorkerProcessSupervisor>());
            var cutoff = VisibilityCutoff(services);
            var pending = updates.Where(p => IsVisible(p, cutoff)).ToList();
            var currentPage = Math.Max(0, page ?? 0);
            var pageSize = Math.Clamp(page_size ?? 50, 1, 100);
            try
            {
                var issues = db.OperationalIssues.AsNoTracking();
                var query = state == "resolved" ? issues.Where(i => i.ResolvedAt != null) : VisibleActive(issues, cutoff, updates);
                if (category != null) query = query.Where(i => i.Category == category);
                if (severity != null) query = query.Where(i => i.Severity == severity);
                if (!string.IsNullOrWhiteSpace(instance)) query = query.Where(i => i.ArrInstance == instance || i.ClientInstance == instance);
                if (!string.IsNullOrWhiteSpace(q)) query = query.Where(i => i.Title.Contains(q) || i.Impact.Contains(q) || i.SubjectId.Contains(q));
                var grouped = query.GroupBy(i => new { i.Code, i.Category, i.ArrInstance, i.ClientInstance })
                    .Select(g => new
                    {
                        id = g.Min(i => i.Id),
                        g.Key.Code,
                        g.Key.Category,
                        g.Key.ArrInstance,
                        g.Key.ClientInstance,
                        severity = g.Any(i => i.Severity == "error") ? "error" : "warning",
                        title = g.Max(i => i.Title),
                        impact = g.Max(i => i.Impact),
                        remediation = g.Max(i => i.Remediation),
                        target = g.Max(i => i.Target),
                        firstObserved = g.Min(i => i.FirstObserved),
                        lastObserved = g.Max(i => i.LastObserved),
                        resolvedAt = g.Max(i => i.ResolvedAt),
                        affected = g.Count(),
                        occurrences = g.Sum(i => i.Occurrences)
                    });
                var total = await grouped.CountAsync(ct);
                var groups = await grouped.OrderBy(g => g.severity == "error" ? 0 : 1).ThenByDescending(g => g.lastObserved)
                    .Skip((int)Math.Min((long)currentPage * pageSize, int.MaxValue)).Take(pageSize).ToListAsync(ct);
                var items = new List<object>();
                var subjects = new List<OperationalIssue>();
                foreach (var group in groups)
                {
                    var examples = await query.Where(i => i.Code == group.Code && i.ArrInstance == group.ArrInstance && i.ClientInstance == group.ClientInstance)
                        .OrderByDescending(i => i.LastObserved).Take(5).ToListAsync(ct);
                    subjects.AddRange(examples);
                    items.Add(new
                    {
                        group.id,
                        group.Code,
                        group.Category,
                        group.ArrInstance,
                        group.ClientInstance,
                        group.severity,
                        group.title,
                        impact = group.affected == 1 ? group.impact : $"{group.affected} affected subjects. Expand each subject below for its own failure evidence.",
                        remediation = group.affected == 1 ? group.remediation : "Follow the specific remediation shown for each affected subject.",
                        group.target,
                        group.firstObserved,
                        group.lastObserved,
                        group.resolvedAt,
                        group.affected,
                        group.occurrences,
                        examples
                    });
                }
                await DescribeSubjectsAsync(subjects, services, ct);
                return Results.Ok(new { available = true, items, total, page = currentPage, pageSize, pending });
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return Results.Ok(new { available = false, items = Array.Empty<object>(), total = (int?)null, page = currentPage, pageSize, pending }); }
        });
        app.MapGet(prefix + "/issues/{id:long}", async (long id, TorrentarrDbContext db, IOperationalIssueReporter reporter, IServiceProvider services, int? page, int? page_size, CancellationToken ct) =>
        {
            try
            {
                var cutoff = VisibilityCutoff(services);
                var updates = Pending(reporter, services.GetService<WorkerProcessSupervisor>());
                var issues = db.OperationalIssues.AsNoTracking();
                var visible = issues.Where(i => i.ResolvedAt != null).Union(VisibleActive(issues, cutoff, updates));
                var issue = await visible.SingleOrDefaultAsync(i => i.Id == id, ct);
                if (issue == null) return Results.NotFound();
                var query = visible.Where(i => i.Code == issue.Code && i.ArrInstance == issue.ArrInstance && i.ClientInstance == issue.ClientInstance);
                var current = Math.Max(0, page ?? 0); var size = Math.Clamp(page_size ?? 50, 1, 100);
                var items = await query.OrderBy(i => i.ResolvedAt != null).ThenByDescending(i => i.LastObserved).Skip((int)Math.Min((long)current * size, int.MaxValue)).Take(size).ToListAsync(ct);
                await DescribeSubjectsAsync(items, services, ct);
                return Results.Ok(new { available = true, total = await query.CountAsync(ct), items, page = current, pageSize = size });
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return Results.Json(new { available = false, error = "Issue history unavailable." }, statusCode: 503); }
        });
    }
}
