namespace Commandarr.Core.Interfaces;

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
}
