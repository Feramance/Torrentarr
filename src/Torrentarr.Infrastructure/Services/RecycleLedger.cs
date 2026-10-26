using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

internal sealed class RecycleLedger(IServiceScopeFactory? scopes)
{
    public static string Key(string client, string destination) => MaintenancePersistence.Key(client, "",
        "recycle:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? Path.GetFullPath(destination).ToUpperInvariant() : Path.GetFullPath(destination)))));

    public List<RecycleEntry> Read()
    {
        using var scope = Scope();
        return scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>().MaintenanceState.AsNoTracking()
            .Where(s => s.Key.Contains("|recycle:")).ToList()
            .Select(s =>
            {
                var entry = JsonConvert.DeserializeObject<RecycleEntry>(s.Data) ?? throw new InvalidDataException("Invalid recycle evidence; purge blocked.");
                if (string.IsNullOrWhiteSpace(entry.ClientInstanceId) || !Path.IsPathFullyQualified(entry.Destination)
                    || entry.RecycledAt == default || entry.Key != s.Key || entry.Key != Key(entry.ClientInstanceId, entry.Destination)
                    || entry.State is not ("preparing" or "committed" or "needs-recovery" or "restored" or "purged"))
                    throw new InvalidDataException("Invalid recycle identity or timestamp; purge blocked.");
                return entry;
            }).ToList();
    }
    public RecycleEntry Prepare(string client, string source, string destination, string? hash = null)
    {
        var entry = new RecycleEntry(Key(client, destination), client, source, destination, DateTimeOffset.UtcNow, "preparing", hash);
        Write(entry); return entry;
    }
    public void Write(RecycleEntry entry)
    {
        using var scope = Scope();
        MaintenancePersistence.Write(scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>(), entry.Key, entry);
    }
    private IServiceScope Scope() => scopes?.CreateScope() ?? throw new InvalidOperationException("Durable recycle storage is unavailable; content is preserved.");
}
