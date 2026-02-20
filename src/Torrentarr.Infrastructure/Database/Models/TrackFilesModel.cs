using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

/// <summary>
/// Track files model matching qBitrr's Peewee schema for Lidarr
/// </summary>
[Table("trackfilesmodel")]
public class TrackFilesModel
{
    [Key]
    [Column("entryid")]
    public int EntryId { get; set; }

    [Column("albumid")]
    public int AlbumId { get; set; }

    [Column("tracknumber")]
    public int? TrackNumber { get; set; }

    [Column("title")]
    public string? Title { get; set; }

    [Column("arrinstance")]
    public string ArrInstance { get; set; } = "";

    [Column("duration")]
    public int? Duration { get; set; }

    [Column("hasfile")]
    public bool HasFile { get; set; }

    [Column("trackfileid")]
    public int? TrackFileId { get; set; }

    [Column("monitored")]
    public bool Monitored { get; set; }
}
