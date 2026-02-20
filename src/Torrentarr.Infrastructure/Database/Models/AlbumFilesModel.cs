using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

/// <summary>
/// Album files model matching qBitrr's Peewee schema for Lidarr
/// </summary>
[Table("albumfilesmodel")]
public class AlbumFilesModel
{
    [Key]
    [Column("entryid")]
    public int EntryId { get; set; }

    [Column("title")]
    public string Title { get; set; } = "";

    [Column("monitored")]
    public bool Monitored { get; set; }

    [Column("foreignalbumid")]
    public string ForeignAlbumId { get; set; } = "";

    [Column("releasedate")]
    public DateTime? ReleaseDate { get; set; }

    [Column("arrinstance")]
    public string ArrInstance { get; set; } = "";

    [Column("searched")]
    public bool Searched { get; set; }

    [Column("albumfileid")]
    public int AlbumFileId { get; set; }

    [Column("isrequest")]
    public bool IsRequest { get; set; }

    [Column("qualitymet")]
    public bool QualityMet { get; set; }

    [Column("upgrade")]
    public bool Upgrade { get; set; }

    [Column("customformatscore")]
    public int? CustomFormatScore { get; set; }

    [Column("mincustomformatscore")]
    public int? MinCustomFormatScore { get; set; }

    [Column("customformatmet")]
    public bool CustomFormatMet { get; set; }

    [Column("reason")]
    public string? Reason { get; set; }

    [Column("artistid")]
    public int ArtistId { get; set; }

    [Column("artisttitle")]
    public string? ArtistTitle { get; set; }

    [Column("qualityprofileid")]
    public int? QualityProfileId { get; set; }

    [Column("qualityprofilename")]
    public string? QualityProfileName { get; set; }

    [Column("lastprofileswitchtime")]
    public DateTime? LastProfileSwitchTime { get; set; }

    [Column("currentprofileid")]
    public int? CurrentProfileId { get; set; }

    [Column("originalprofileid")]
    public int? OriginalProfileId { get; set; }
}
