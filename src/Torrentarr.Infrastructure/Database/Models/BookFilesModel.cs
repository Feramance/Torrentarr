using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

/// <summary>
/// Book files model matching qBitrr's Peewee schema for Readarr (author → book, no track layer).
/// </summary>
[Table("bookfilesmodel")]
public class BookFilesModel
{
    [Key]
    [Column("entryid")]
    public int EntryId { get; set; }

    [Column("title")]
    public string Title { get; set; } = "";

    [Column("monitored")]
    public bool Monitored { get; set; }

    [Column("foreignbookid")]
    public string ForeignBookId { get; set; } = "";

    [Column("releasedate")]
    public DateTime? ReleaseDate { get; set; }

    [Column("arrinstance")]
    public string ArrInstance { get; set; } = "";

    [Column("searched")]
    public bool Searched { get; set; }

    [Column("bookfileid")]
    public int BookFileId { get; set; }

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

    [Column("authorid")]
    public int AuthorId { get; set; }

    [Column("authortitle")]
    public string? AuthorTitle { get; set; }

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

    [Column("hasfile")]
    public bool HasFile { get; set; }

    [Column("arrauthorid")]
    public int ArrAuthorId { get; set; }

    [Column("incinemas")]
    public DateTime? InCinemas { get; set; }

    [Column("digitalrelease")]
    public DateTime? DigitalRelease { get; set; }

    [Column("physicalrelease")]
    public DateTime? PhysicalRelease { get; set; }

    [Column("minimumavailability")]
    public string? MinimumAvailability { get; set; }
}
