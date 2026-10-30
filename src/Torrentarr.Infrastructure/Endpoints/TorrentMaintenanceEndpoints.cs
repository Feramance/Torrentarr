using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.Endpoints;

public static class TorrentMaintenanceEndpoints
{
    public static void MapTorrentMaintenanceEndpoints(this WebApplication app, string prefix, bool standalone = false)
    {
        app.MapGet(prefix + "/maintenance/status", (IMaintenanceCoordinator cleanup) => Results.Ok(cleanup.GetStatus()));
        app.MapGet(prefix + "/maintenance/history", (IMaintenanceCoordinator cleanup, int? limit) => Results.Ok(cleanup.GetHistory(limit ?? 50)));
        app.MapGet(prefix + "/torrent-automation/status", (TorrentAutomationService automation) => Results.Ok(automation.GetStatus()));
        app.MapPost(prefix + "/maintenance/preview", async (MaintenancePreviewRequest request, IMaintenanceCoordinator cleanup, IOperationalIssueReporter issues, Torrentarr.Infrastructure.Database.TorrentarrDbContext db, CancellationToken ct) =>
        {
            if (request.ClientInstanceIds == null || request.Operations == null || request.MaintenanceOverrides == null || request.Operations.Any(op => !Enum.IsDefined(op)))
                return Results.BadRequest(new { error = "Invalid cleanup selection." });
            try
            {
                var preview = await cleanup.PreviewAsync(request, ct);
                var selection = string.Join(",", preview.Operations.Order());
                foreach (var diagnostic in preview.Diagnostics)
                    await issues.ReportAsync(new("maintenance.preview." + diagnostic.Code, "automation", "Maintenance preview needs attention",
                        "The requested preview could not validate all requirements; affected actions remain blocked.",
                        "Open Manage and review the preview diagnostics before applying changes.", ClientInstance: diagnostic.ClientInstanceId,
                        SubjectType: "preview", SubjectId: selection, Severity: diagnostic.Severity, Target: "maintenance"), ct);
                try
                {
                    var verifiedClients = preview.CriticalErrors.Count == 0
                        ? preview.OperationResults.GroupBy(r => r.ClientInstanceId).Where(g => g.All(r => r.State == "ready")).Select(g => g.Key).ToList() : [];
                    var previous = await db.OperationalIssues.Where(i => i.SubjectType == "preview" && i.SubjectId == selection && i.ResolvedAt == null && verifiedClients.Contains(i.ClientInstance)).ToListAsync(ct);
                    foreach (var old in previous.Where(i => !preview.Diagnostics.Any(d => "maintenance.preview." + d.Code == i.Code && d.ClientInstanceId == i.ClientInstance)))
                        await issues.ResolveAsync(new(old.Code, old.Category, old.Title, old.Impact, old.Remediation, old.ArrInstance, old.ClientInstance, old.SubjectType, old.SubjectId, old.Severity, old.Target), ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested) { /* Diagnostics persistence must not change preview results. */ }
                return Results.Ok(preview);
            }
            catch (NotSupportedException ex) { return Results.Json(new { error = ex.Message }, statusCode: 410); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost(prefix + "/maintenance/apply", async (MaintenanceApplyRequest request, IMaintenanceCoordinator cleanup, ConfigurationLoader loader, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.PlanId)) return Results.BadRequest(new { error = "A reviewed preview is required." });
            try
            {
                using var ownership = standalone ? HostConfigOwnership.Acquire(loader.ConfigPath) : null;
                return Results.Ok(request.AuthorizeOnce ? await cleanup.ApplyOnceAsync(request.PlanId, ct) : await cleanup.ApplyAsync(request.PlanId, ct));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        foreach (var command in new[] { "arm", "disarm" })
            app.MapPost(prefix + "/maintenance/" + command, () => Results.Json(new
            {
                error = "Temporary session authorization has been removed. Save Automatic mode to enable scheduling, or Manual mode to stop scheduling."
            }, statusCode: StatusCodes.Status410Gone));
        app.MapPost(prefix + "/maintenance/run", async (MaintenanceClientsRequest request, IMaintenanceCoordinator coordinator, ConfigurationLoader loader, CancellationToken ct) =>
        {
            if (request.ClientInstanceIds == null || request.Operations == null || request.Operations.Any(op => !Enum.IsDefined(op)))
                return Results.BadRequest(new { error = "Invalid maintenance selection." });
            try
            {
                using var ownership = standalone ? HostConfigOwnership.Acquire(loader.ConfigPath) : null;
                return Results.Ok(await coordinator.RunAsync(request.ClientInstanceIds.Count == 0 ? null : request.ClientInstanceIds, ct: ct, operations: request.Operations.Count == 0 ? null : request.Operations));
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost(prefix + "/maintenance/cancel", (IMaintenanceCoordinator cleanup) => { cleanup.Cancel(); return Results.Accepted(); });
    }
}
