using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Services;

internal sealed record SearchCatalogState(string Server, string Category, long At, bool Ready, string Version,
    string? Settings = null, int? Year = null)
{
    internal static string ObservationKey(string instance) => MaintenancePersistence.Key(instance, "", "search-catalog");
    internal static string YearKey(string instance) => MaintenancePersistence.Key(instance, "", "search-year");

    internal static async Task<SearchCatalogState?> ReadAsync(TorrentarrDbContext db, string key, CancellationToken ct)
    {
        var raw = await db.MaintenanceState.AsNoTracking().Where(row => row.Key == key).Select(row => row.Data).SingleOrDefaultAsync(ct);
        if (raw == null) return null;
        try { return JsonConvert.DeserializeObject<SearchCatalogState>(raw); }
        catch (JsonException) { return null; }
    }
}
