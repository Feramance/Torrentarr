using Microsoft.EntityFrameworkCore;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Tagless-mode helpers for persisting qBitrr tag semantics in TorrentLibrary columns.
/// </summary>
public static class TaglessTorrentLibraryHelper
{
    public const string IgnoredTag = "qBitrr-ignored";
    public const string AllowedSeedingTag = "qBitrr-allowed_seeding";
    public const string AllowedStalledTag = "qBitrr-allowed_stalled";
    public const string FreeSpacePausedTag = "qBitrr-free_space_paused";

    /// <summary>
    /// Read tagless tag state for a torrent row scoped to <paramref name="qbitInstance"/> when set.
    /// </summary>
    public static bool HasTaglessTag(
        TorrentarrDbContext db,
        string hash,
        string? qbitInstance,
        string tag)
    {
        if (tag.Equals(IgnoredTag, StringComparison.OrdinalIgnoreCase))
            return false;

        IQueryable<TorrentLibrary> query = db.TorrentLibrary.AsNoTracking().Where(t => t.Hash == hash);
        if (!string.IsNullOrEmpty(qbitInstance))
            query = query.Where(t => t.QbitInstance == qbitInstance);

        var dbEntry = query.FirstOrDefault();
        if (dbEntry == null)
            return false;

        return tag switch
        {
            FreeSpacePausedTag => dbEntry.FreeSpacePaused,
            AllowedSeedingTag => dbEntry.AllowedSeeding,
            AllowedStalledTag => dbEntry.AllowedStalled,
            _ => false
        };
    }

    /// <summary>
    /// Set <see cref="TorrentLibrary.FreeSpacePaused"/> in tagless mode.
    /// When pausing, upserts a row if the torrent is not yet in the database — otherwise
    /// the update is a no-op and TorrentProcessor resumes the free-space pause.
    /// </summary>
    public static async Task SetFreeSpacePausedAsync(
        TorrentarrDbContext db,
        string hash,
        string category,
        string qbitInstance,
        bool paused,
        CancellationToken ct = default)
    {
        var entry = await db.TorrentLibrary
            .FirstOrDefaultAsync(t => t.Hash == hash && t.QbitInstance == qbitInstance, ct);

        if (entry != null)
        {
            entry.FreeSpacePaused = paused;
            await db.SaveChangesAsync(ct);
            return;
        }

        if (!paused)
            return;

        db.TorrentLibrary.Add(new TorrentLibrary
        {
            Hash = hash,
            Category = category,
            QbitInstance = qbitInstance,
            FreeSpacePaused = true
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // TorrentProcessor.EnsureTorrentInDatabaseAsync may insert the same row concurrently.
            db.ChangeTracker.Clear();
            entry = await db.TorrentLibrary
                .FirstOrDefaultAsync(t => t.Hash == hash && t.QbitInstance == qbitInstance, ct);
            if (entry == null)
                throw;

            entry.FreeSpacePaused = true;
            await db.SaveChangesAsync(ct);
        }
    }
}
