using Microsoft.EntityFrameworkCore;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Database;

/// <summary>
/// Tagless-mode free-space pause persistence. ExecuteUpdate alone is a no-op when no
/// TorrentLibrary row exists yet; upsert ensures TorrentProcessor won't auto-resume.
/// </summary>
public static class TorrentLibraryFreeSpaceHelper
{
    public static async Task SetFreeSpacePausedAsync(
        TorrentarrDbContext db,
        string hash,
        string category,
        string qbitInstance,
        bool paused,
        CancellationToken ct = default)
    {
        var updated = await db.TorrentLibrary
            .Where(t => t.Hash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.FreeSpacePaused, paused), ct);

        if (updated == 0 && paused)
        {
            db.TorrentLibrary.Add(new TorrentLibrary
            {
                Hash = hash,
                Category = category,
                QbitInstance = qbitInstance,
                FreeSpacePaused = true,
                Imported = false,
                AllowedSeeding = false,
                AllowedStalled = false,
            });
            await db.SaveChangesAsync(ct);
        }
    }
}
