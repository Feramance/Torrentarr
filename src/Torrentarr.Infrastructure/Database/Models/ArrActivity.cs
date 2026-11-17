using System.ComponentModel.DataAnnotations.Schema;
namespace Torrentarr.Infrastructure.Database.Models;

[Table("torrentarr_arr_samples")]
public sealed class ArrActivitySample
{
    public string Instance { get; set; } = "";
    public long Minute { get; set; }
    public string Type { get; set; } = "";
    public int? QueueDepth { get; set; }
    public bool CompletionObserved { get; set; }
}

[Table("torrentarr_arr_operations")]
public sealed class ArrActivityOperation
{
    public string Instance { get; set; } = "";
    public int CommandId { get; set; }
    public string Type { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Server { get; set; } = "";
    public string ClientInstance { get; set; } = "";
    public string DownloadId { get; set; } = "";
    public long SubmittedAt { get; set; }
    public long? ConfirmedAt { get; set; }
    public string Outcome { get; set; } = "pending";
    public string? EvidenceId { get; set; }
}
