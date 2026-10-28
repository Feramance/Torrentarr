using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Torrentarr.Infrastructure.Database.Models;

[Table("torrentarr_sonarr_search_retries")]
[PrimaryKey(nameof(ArrInstance), nameof(DownloadId))]
public sealed class SonarrSearchRetry
{
    public string ArrInstance { get; set; } = "";
    public string DownloadId { get; set; } = "";
    public string CandidatesJson { get; set; } = "[]";
}
