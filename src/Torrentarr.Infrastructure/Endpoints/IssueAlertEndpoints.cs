using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.Endpoints;

public static class IssueAlertEndpoints
{
    public static void MapIssueAlertTest(this WebApplication app, string prefix) =>
        app.MapPost(prefix + "/issues/alerts/test", async (TorrentarrConfig config, CancellationToken ct) =>
        {
            var destination = ConfigValidationHelper.IssueAlertDestination(config.Settings);
            if (destination == null) return Results.BadRequest(new { error = "Save a webhook destination before sending a test." });
            var payload = new IssueAlertDispatcher.Payload(Guid.NewGuid().ToString("N"), "test", "info", "",
                "test", "Torrentarr operational issue alert test.", null);
            return await IssueAlertDispatcher.SendAsync(destination, payload, ct)
                ? Results.Ok(new { success = true })
                : Results.Json(new { error = "The webhook did not accept the test event." }, statusCode: 502);
        });
}
