using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

internal static class MaintenancePersistence
{
    public static string Key(string client, string hash, string kind) => $"{client.ToUpperInvariant()}|{hash.ToUpperInvariant()}|{kind}";
    public static T Read<T>(TorrentarrDbContext db, string key) where T : new()
    {
        var json = db.MaintenanceState.AsNoTracking().Where(s => s.Key == key).Select(s => s.Data).FirstOrDefault();
        return json == null ? new() : JsonConvert.DeserializeObject<T>(json) ?? throw new InvalidDataException("Invalid maintenance state; refusing to discard protection evidence.");
    }
    public static void Write<T>(TorrentarrDbContext db, string key, T state)
    {
        var json = JsonConvert.SerializeObject(state);
        if (!db.Database.IsRelational())
        {
            var record = db.MaintenanceState.Find(key);
            if (record == null) db.MaintenanceState.Add(new() { Key = key, Data = json });
            else record.Data = json;
            db.SaveChanges(); return;
        }
        db.Database.ExecuteSqlInterpolated($"INSERT INTO torrentarr_maintenance_state (Key, Data) VALUES ({key}, {json}) ON CONFLICT(Key) DO UPDATE SET Data=excluded.Data");
    }
}

internal sealed class ScanCursor { public string Position { get; set; } = ""; }
internal sealed class TagOwnership { public bool Owned { get; set; } public long AddedOn { get; set; } }

internal sealed class PauseOwnership { public bool Owned { get; set; } public long AddedOn { get; set; } }
