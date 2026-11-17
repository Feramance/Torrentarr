using System.Collections.Concurrent;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Per-instance runtime state tracked by ArrWorkerManager and exposed via /web/processes
/// </summary>
public class ArrProcessState
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Alive { get; set; }
    public bool Rebuilding { get; set; }
    public int? Pid { get; set; }
    public int? SearchesTriggered { get; set; }
    public int? ItemsSearched { get; set; }
    public int? DownloadingCount { get; set; }
    public int? SeedingCount { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public string? ObservationError { get; set; }
    public int? PausedLastPass { get; set; }
    public int? ResumedLastPass { get; set; }
    public int? MonitoredCategoryCount { get; set; }
    public int? HandledLastPass { get; set; }
    public string? SearchSummary { get; set; }
    public string? SearchTimestamp { get; set; }
    public int? QueueCount { get; set; }
    public int? CategoryCount { get; set; }
    public string? MetricType { get; set; }
    public string? Status { get; set; }
}

/// <summary>
/// Singleton that tracks per-Arr-instance runtime state. Thread-safe.
/// Populated by ArrWorkerManager and by the Host's ProcessOrchestratorService (Recheck, Failed, FreeSpaceManager).
/// Read by /web/processes and /api/processes endpoints.
/// </summary>
public class ProcessStateManager
{
    private readonly ConcurrentDictionary<string, ArrProcessState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public void Initialize(string instanceName, ArrProcessState state) =>
        _states[instanceName] = state;

    public void Update(string instanceName, Action<ArrProcessState> update)
    {
        if (_states.TryGetValue(instanceName, out var state))
            update(state);
    }

    public ArrProcessState? GetState(string instanceName) =>
        _states.TryGetValue(instanceName, out var s) ? s : null;

    public ICollection<ArrProcessState> GetAll() => _states.Values;
}
