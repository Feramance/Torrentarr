using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

[Table("torrentarr_transfer_samples")]
public sealed class TransferSample
{
    public string ClientInstance { get; set; } = "";
    public long Minute { get; set; }
    public long DownloadRate { get; set; }
    public long UploadRate { get; set; }
}
