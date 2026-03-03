using Torrentarr.Infrastructure.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace Torrentarr.Infrastructure.Database;

/// <summary>
/// EF Core DbContext for Torrentarr
/// Matches qBitrr's SQLite database schema exactly for backwards compatibility
/// </summary>
public class TorrentarrDbContext : DbContext
{
    public TorrentarrDbContext(DbContextOptions<TorrentarrDbContext> options)
        : base(options)
    {
    }

    public DbSet<MoviesFilesModel> Movies { get; set; }
    public DbSet<EpisodeFilesModel> Episodes { get; set; }
    public DbSet<SeriesFilesModel> Series { get; set; }
    public DbSet<AlbumFilesModel> Albums { get; set; }
    public DbSet<TrackFilesModel> Tracks { get; set; }
    public DbSet<ArtistFilesModel> Artists { get; set; }
    public DbSet<TorrentLibrary> TorrentLibrary { get; set; }
    public DbSet<MovieQueueModel> MovieQueue { get; set; }
    public DbSet<EpisodeQueueModel> EpisodeQueue { get; set; }
    public DbSet<AlbumQueueModel> AlbumQueue { get; set; }
    public DbSet<FilesQueued> FilesQueued { get; set; }
    public DbSet<SearchActivity> SearchActivity { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure table names to match qBitrr (lowercase)
        modelBuilder.Entity<MoviesFilesModel>().ToTable("moviesfilesmodel");
        modelBuilder.Entity<EpisodeFilesModel>().ToTable("episodefilesmodel");
        modelBuilder.Entity<SeriesFilesModel>().ToTable("seriesfilesmodel");
        modelBuilder.Entity<AlbumFilesModel>().ToTable("albumfilesmodel");
        modelBuilder.Entity<TrackFilesModel>().ToTable("trackfilesmodel");
        modelBuilder.Entity<ArtistFilesModel>().ToTable("artistfilesmodel");
        modelBuilder.Entity<TorrentLibrary>().ToTable("torrentlibrary");
        modelBuilder.Entity<MovieQueueModel>().ToTable("moviequeuemodel");
        modelBuilder.Entity<EpisodeQueueModel>().ToTable("episodequeuemodel");
        modelBuilder.Entity<AlbumQueueModel>().ToTable("albumqueuemodel");
        modelBuilder.Entity<FilesQueued>().ToTable("filesqueued");
        modelBuilder.Entity<SearchActivity>().ToTable("searchactivity");

        // Configure unique index for TorrentLibrary (Hash, QbitInstance)
        modelBuilder.Entity<TorrentLibrary>()
            .HasIndex(t => new { t.Hash, t.QbitInstance })
            .IsUnique();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            // Default connection string (will be overridden by DI)
            var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dbPath = Path.Combine(homePath, ".config", "torrentarr", "torrentarr.db");
            optionsBuilder.UseSqlite($"Data Source={dbPath}");

            // SQLite-specific options — only when we're setting up the default connection.
            // Skipped when a test or other caller overrides via DI (InMemory, named SQLite, etc.).
            optionsBuilder.UseSqlite(options =>
            {
                options.CommandTimeout(30);
            });
        }
    }
}

/// <summary>
/// Extension methods for configuring the database with WAL mode
/// </summary>
public static class DbContextExtensions
{
    public static void ConfigureWalMode(this TorrentarrDbContext context)
    {
        // WAL mode pragmas are SQLite-only. Skip for InMemory or other test providers.
        if (context.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
            return;

        // Enable WAL (Write-Ahead Logging) mode for better concurrency
        context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        context.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
        context.Database.ExecuteSqlRaw("PRAGMA temp_store=MEMORY;");
        context.Database.ExecuteSqlRaw("PRAGMA mmap_size=30000000000;");
        context.Database.ExecuteSqlRaw("PRAGMA page_size=4096;");
        context.Database.ExecuteSqlRaw("PRAGMA cache_size=10000;");
    }
}
