using Commandarr.Infrastructure.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace Commandarr.Infrastructure.Database;

/// <summary>
/// EF Core DbContext for Commandarr
/// Matches qBitrr's SQLite database schema exactly for backwards compatibility
/// </summary>
public class CommandarrDbContext : DbContext
{
    public CommandarrDbContext(DbContextOptions<CommandarrDbContext> options)
        : base(options)
    {
    }

    public DbSet<MoviesFilesModel> Movies { get; set; }
    public DbSet<EpisodeFilesModel> Episodes { get; set; }
    public DbSet<TorrentLibrary> TorrentLibrary { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure table names to match qBitrr (lowercase)
        modelBuilder.Entity<MoviesFilesModel>().ToTable("moviesfilesmodel");
        modelBuilder.Entity<EpisodeFilesModel>().ToTable("episodefilesmodel");
        modelBuilder.Entity<TorrentLibrary>().ToTable("torrentlibrary");

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
            var dbPath = Path.Combine(homePath, ".config", "commandarr", "qbitrr.db");
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }

        // Enable WAL mode for multi-process access
        optionsBuilder.UseSqlite(options =>
        {
            options.CommandTimeout(30);
        });
    }
}

/// <summary>
/// Extension methods for configuring the database with WAL mode
/// </summary>
public static class DbContextExtensions
{
    public static void ConfigureWalMode(this CommandarrDbContext context)
    {
        // Enable WAL (Write-Ahead Logging) mode for better concurrency
        context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        context.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
        context.Database.ExecuteSqlRaw("PRAGMA temp_store=MEMORY;");
        context.Database.ExecuteSqlRaw("PRAGMA mmap_size=30000000000;");
        context.Database.ExecuteSqlRaw("PRAGMA page_size=4096;");
        context.Database.ExecuteSqlRaw("PRAGMA cache_size=10000;");
    }
}
