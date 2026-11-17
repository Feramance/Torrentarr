using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using RestSharp;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

/// <summary>Narrow execution-context scopes attribute real command responses to their submitter.</summary>
internal sealed class ArrActivityTracking : IDisposable
{
    private sealed record Context(TorrentarrDbContext? Db, ILogger? Logger, string Instance,
        ArrInstanceConfig? Config, string Kind, string Hash, string Client);
    private static readonly AsyncLocal<Context?> Current = new();
    private readonly Context? _previous;
    private ArrActivityTracking(Context value) { _previous = Current.Value; Current.Value = value; }
    internal static IDisposable ForClient(string? client) => new ArrActivityTracking(
        new(null, null, "", null, "", "", client ?? ""));
    internal static IDisposable Begin(TorrentarrDbContext db, ILogger logger, string instance,
        ArrInstanceConfig config, string kind, string hash = "") => new ArrActivityTracking(
            new(db, logger, instance, config, kind, hash, Current.Value?.Client ?? ""));
    public void Dispose() => Current.Value = _previous;
    internal static string ServerKey(ArrInstanceConfig config) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(config.URI.TrimEnd('/'))));

    internal static async Task RecordAcceptedAsync(RestRequest request, RestResponse response, CancellationToken ct)
    {
        var context = Current.Value;
        if (context?.Db == null || context.Config == null || !context.Db.Database.IsSqlite()
            || request.Method != Method.Post || !request.Resource.TrimEnd('/').EndsWith("/command") || !response.IsSuccessful) return;
        try
        {
            var id = JObject.Parse(response.Content ?? "{}")["id"]?.Value<int>() ?? 0;
            if (id <= 0) return;
            var server = ServerKey(context.Config);
            var submitted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var hash = context.Hash.ToUpperInvariant();
            var previousTimeout = context.Db.Database.GetCommandTimeout();
            try
            {
                // Optional statistics must not hold up normal worker writes on a busy SQLite database.
                context.Db.Database.SetCommandTimeout(1);
                await context.Db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO torrentarr_arr_operations (Instance,CommandId,Type,Kind,Server,ClientInstance,DownloadId,SubmittedAt,Outcome) VALUES ({context.Instance},{id},{context.Config.Type},{context.Kind},{server},{context.Client},{hash},{submitted},'pending')", ct);
            }
            finally { context.Db.Database.SetCommandTimeout(previousTimeout); }
        }
        catch (Exception ex) { context.Logger?.LogDebug(ex, "Activity submission tracking unavailable for {Instance}", context.Instance); }
    }
}
