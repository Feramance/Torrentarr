using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;

namespace Torrentarr.Infrastructure.Endpoints;

public static class FailedTorrentDestinationEndpoints
{
    private sealed record CategoryOption(string Name, string? ArrOwner, string? UnavailableReason);
    private sealed record ClientOptions(string Client, CategoryOption[] Categories, bool Available);

    public static void MapFailedTorrentDestinations(this WebApplication app, string prefix) =>
        app.MapGet(prefix + "/config/failed-torrent-destinations", async (
            string? category, TorrentarrConfig config, ITorrentClientRegistry registry, CancellationToken ct) =>
        {
            using var snapshot = config.BeginOperation();
            var source = new ArrInstanceConfig { Category = category ?? "" };
            var clients = await Task.WhenAll(config.GetAllTorrentClients()
                .Where(pair => FailedTorrentRouting.InScope(pair.Value)).Select(async pair =>
                {
                    try
                    {
                        var client = registry.GetClient(pair.Key);
                        if (client == null || !client.Capabilities.Categories) return new ClientOptions(pair.Key, [], false);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        var categories = await client.GetCategoriesAsync(timeout.Token);
                        return new ClientOptions(pair.Key, categories.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(name =>
                        {
                            var owner = CategoryOwnershipHelper.ResolveOwningCategory(config, name, pair.Key);
                            return new CategoryOption(name, owner == null ? null : CategoryOwnershipHelper.FindArrSectionForCategory(config, owner),
                                FailedTorrentRouting.DestinationError(config, source, pair.Key, name));
                        }).ToArray(), true);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception) { return new ClientOptions(pair.Key, [], false); }
                }));
            return Results.Ok(clients);
        });
}
