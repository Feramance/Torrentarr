using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

[Table("torrentarr_retirement_upload")]
public sealed class RetirementUploadObservation
{
    public string Client { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Identity { get; set; } = "";
    public string Category { get; set; } = "";
    public long AddedOn { get; set; }
    public long Uploaded { get; set; }
    public long Seen { get; set; }
    public long Since { get; set; }
}
