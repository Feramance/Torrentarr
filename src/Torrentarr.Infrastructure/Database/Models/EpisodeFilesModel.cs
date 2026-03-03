using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

/// <summary>
/// Episode files model matching qBitrr's Peewee schema
/// </summary>
[Table("episodefilesmodel")]
public class EpisodeFilesModel
{
    [Key]
    [Column("entryid")]
    public int EntryId { get; set; }

    [Column("seriestitle")]
    public string? SeriesTitle { get; set; }

    [Column("title")]
    public string? Title { get; set; }

    [Column("seriesid")]
    public int SeriesId { get; set; }

    [Column("arrinstance")]
    public string ArrInstance { get; set; } = "";

    [Column("episodefileid")]
    public int? EpisodeFileId { get; set; }

    [Column("episodenumber")]
    public int EpisodeNumber { get; set; }

    [Column("seasonnumber")]
    public int SeasonNumber { get; set; }

    [Column("absoluteepisodenumber")]
    public int? AbsoluteEpisodeNumber { get; set; }

    [Column("sceneabsoluteepisodenumber")]
    public int? SceneAbsoluteEpisodeNumber { get; set; }

    [Column("airdateutc")]
    public DateTime? AirDateUtc { get; set; }

    [Column("monitored")]
    public bool? Monitored { get; set; }

    [Column("searched")]
    public bool Searched { get; set; }

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

    [Column("arrseriesid")]
    public int ArrSeriesId { get; set; }

    [Column("incinemas")]
    public DateTime? InCinemas { get; set; }

    [Column("digitalrelease")]
    public DateTime? DigitalRelease { get; set; }

    [Column("physicalrelease")]
    public DateTime? PhysicalRelease { get; set; }

    [Column("minimumavailability")]
    public string? MinimumAvailability { get; set; }
}
