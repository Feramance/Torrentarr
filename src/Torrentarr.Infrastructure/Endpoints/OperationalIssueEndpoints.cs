using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.Endpoints;

public static class OperationalIssueEndpoints
{
    private static IReadOnlyList<PendingOperationalIssue> Pending(IOperationalIssueReporter reporter, WorkerProcessSupervisor? workers)
        => reporter.Pending.Concat(workers?.GetPendingIssues() ?? []).GroupBy(p => new { p.Report.Code, p.Report.ArrInstance, p.Report.ClientInstance, p.Report.SubjectType, p.Report.SubjectId }).Select(g => g.MaxBy(p => p.ObservedAt)!).ToList();

    public static void MapOperationalIssueEndpoints(this WebApplication app, string prefix)
    {
        app.MapGet(prefix + "/issues/summary", async (TorrentarrDbContext db, IOperationalIssueReporter reporter, IServiceProvider services, CancellationToken ct) =>
        {
            var pending = Pending(reporter, services.GetService(typeof(WorkerProcessSupervisor)) as WorkerProcessSupervisor);
            try
            {
                var counts = await db.OperationalIssues.AsNoTracking().Where(i => i.ResolvedAt == null)
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
            var pending = Pending(reporter, services.GetService(typeof(WorkerProcessSupervisor)) as WorkerProcessSupervisor);
            var currentPage = Math.Max(0, page ?? 0);
            var pageSize = Math.Clamp(page_size ?? 50, 1, 100);
            try
            {
                var query = db.OperationalIssues.AsNoTracking().Where(i => state == "resolved" ? i.ResolvedAt != null : i.ResolvedAt == null);
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
                foreach (var group in groups)
                {
                    var examples = await query.Where(i => i.Code == group.Code && i.ArrInstance == group.ArrInstance && i.ClientInstance == group.ClientInstance)
                        .OrderByDescending(i => i.LastObserved).Take(5).ToListAsync(ct);
                    items.Add(new
                    {
                        group.id,
                        group.Code,
                        group.Category,
                        group.ArrInstance,
                        group.ClientInstance,
                        group.severity,
                        group.title,
                        group.impact,
                        group.remediation,
                        group.target,
                        group.firstObserved,
                        group.lastObserved,
                        group.resolvedAt,
                        group.affected,
                        group.occurrences,
                        examples
                    });
                }
                return Results.Ok(new { available = true, items, total, page = currentPage, pageSize, pending });
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return Results.Ok(new { available = false, items = Array.Empty<object>(), total = (int?)null, page = currentPage, pageSize, pending }); }
        });
        app.MapGet(prefix + "/issues/{id:long}", async (long id, TorrentarrDbContext db, int? page, int? page_size, CancellationToken ct) =>
        {
            try
            {
                var issue = await db.OperationalIssues.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct);
                if (issue == null) return Results.NotFound();
                var query = db.OperationalIssues.AsNoTracking().Where(i => i.Code == issue.Code && i.ArrInstance == issue.ArrInstance && i.ClientInstance == issue.ClientInstance);
                var current = Math.Max(0, page ?? 0); var size = Math.Clamp(page_size ?? 50, 1, 100);
                return Results.Ok(new { available = true, total = await query.CountAsync(ct), items = await query.OrderBy(i => i.ResolvedAt != null).ThenByDescending(i => i.LastObserved).Skip((int)Math.Min((long)current * size, int.MaxValue)).Take(size).ToListAsync(ct), page = current, pageSize = size });
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return Results.Json(new { available = false, error = "Issue history unavailable." }, statusCode: 503); }
        });
    }
}
