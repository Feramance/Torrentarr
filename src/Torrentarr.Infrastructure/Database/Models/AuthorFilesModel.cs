using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

/// <summary>
/// Author files model matching qBitrr's Peewee schema for Readarr.
/// </summary>
[Table("authorfilesmodel")]
public class AuthorFilesModel
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

    [Column("bookcount")]
    public int BookCount { get; set; }

    [Column("mincustomformatscore")]
    public int? MinCustomFormatScore { get; set; }

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

    [Column("arrid")]
    public int ArrId { get; set; }
}
