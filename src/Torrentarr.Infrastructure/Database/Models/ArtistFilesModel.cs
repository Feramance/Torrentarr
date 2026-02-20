using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

/// <summary>
/// Artist files model matching qBitrr's Peewee schema for Lidarr
/// </summary>
[Table("artistfilesmodel")]
public class ArtistFilesModel
{
    [Key]
    [Column("entryid")]
    public int EntryId { get; set; }

    [Column("title")]
    public string? Title { get; set; }

    [Column("monitored")]
    public bool? Monitored { get; set; }

    [Column("arrinstance")]
    public string ArrInstance { get; set; } = "";

    [Column("searched")]
    public bool Searched { get; set; }

    [Column("upgrade")]
    public bool Upgrade { get; set; }

    [Column("mincustomformatscore")]
    public int? MinCustomFormatScore { get; set; }

    [Column("qualityprofileid")]
    public int? QualityProfileId { get; set; }

    [Column("qualityprofilename")]
    public string? QualityProfileName { get; set; }
}
