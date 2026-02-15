using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Commandarr.Infrastructure.Database.Models;

/// <summary>
/// Torrent library model matching qBitrr's Peewee schema
/// Tracks torrent state across qBit instances
/// </summary>
[Table("torrentlibrary")]
[Index(nameof(Hash), nameof(QbitInstance), IsUnique = true)]
public class TorrentLibrary
{
    [Key]
    public int Id { get; set; }

    [Column("hash")]
    [Required]
    public string Hash { get; set; } = "";

    [Column("category")]
    [Required]
    public string Category { get; set; } = "";

    [Column("qbitinstance")]
    [Required]
    public string QbitInstance { get; set; } = "default";

    [Column("arrinstance")]
    public string ArrInstance { get; set; } = "";

    [Column("allowedseeding")]
    public bool AllowedSeeding { get; set; }

    [Column("imported")]
    public bool Imported { get; set; }

    [Column("allowedstalled")]
    public bool AllowedStalled { get; set; }

    [Column("freespacepaused")]
    public bool FreeSpacePaused { get; set; }
}
