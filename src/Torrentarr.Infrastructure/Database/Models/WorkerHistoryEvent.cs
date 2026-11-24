using System.ComponentModel.DataAnnotations.Schema;

namespace Torrentarr.Infrastructure.Database.Models;

[Table("torrentarr_worker_history")]
public sealed class WorkerHistoryEvent
{
    [NotMapped]
    public string LogStream => "worker-" + Torrentarr.Infrastructure.Logging.ServiceLogSink.SafeName(Instance);
    public string RunId { get; set; } = "";
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Instance { get; set; } = "";
    public string Loop { get; set; } = "";
    public string Type { get; set; } = "";
    public string Source { get; set; } = "worker";
    public string SubjectType { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Details { get; set; } = "{}";
    public string ClientInstance { get; set; } = "";
    public string Server { get; set; } = "";
    public int? CommandId { get; set; }
    public long? SubmittedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
