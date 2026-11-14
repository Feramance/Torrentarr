namespace Torrentarr.Core.Interfaces;

/// <summary>
/// Interface for process orchestration and lifecycle management
/// </summary>
public interface IProcessOrchestrator
{
    /// <summary>
    /// Start all configured worker processes
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop all running processes gracefully
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restart a specific worker process by name
    /// </summary>
    Task RestartProcessAsync(string processName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get status of all managed processes
    /// </summary>
    Task<Dictionary<string, ProcessStatus>> GetProcessStatusAsync();
}

/// <summary>
/// Status information for a managed process
/// </summary>
public class ProcessStatus
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Kind { get; set; } = ""; // worker, webui, checkpoint
    public int? ProcessId { get; set; }
    public bool IsAlive { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public int RestartCount { get; set; }
    public int? LastExitCode { get; set; }
    public string? LastFailure { get; set; }
    public bool RestartRequested { get; set; }
    public long ConfigRevision { get; set; }
    public int? SearchesTriggered { get; set; }
    public int? ItemsSearched { get; set; }
    public int? DownloadingCount { get; set; }
    public int? SeedingCount { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public string? ObservationError { get; set; }
    public int? PausedLastPass { get; set; }
    public int? ResumedLastPass { get; set; }
    public int? HandledLastPass { get; set; }
    public string? SearchSummary { get; set; }
    public string? SearchTimestamp { get; set; }
    public int? QueueCount { get; set; }
    public int? CategoryCount { get; set; }
    public string? MetricType { get; set; }
    public string? Status { get; set; }
    public Torrentarr.Core.Models.WorkerLoopProgress? Torrent { get; set; }
    public Torrentarr.Core.Models.WorkerLoopProgress? Search { get; set; }
}
