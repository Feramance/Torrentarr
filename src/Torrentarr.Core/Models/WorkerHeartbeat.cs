using System.Text.Json;

namespace Torrentarr.Core.Models;

public sealed record WorkerLoopProgress(string Phase, DateTimeOffset StartedAt, DateTimeOffset? WakeAt = null, DateTimeOffset? LastSuccess = null)
{
    // shortcut: active cycles are capped at 30 minutes; revisit if large libraries need longer.
    public static readonly TimeSpan CycleTimeout = TimeSpan.FromMinutes(30);
    public bool IsStalled(DateTimeOffset now) => now > (WakeAt ?? StartedAt + CycleTimeout) + TimeSpan.FromSeconds(15);
}

public sealed record WorkerHeartbeat
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public int Version { get; init; } = 2;
    public string Instance { get; init; } = "";
    public int Pid { get; init; }
    public DateTimeOffset Heartbeat { get; init; }
    public bool RestartRequested { get; init; }
    public string? SearchSummary { get; init; }
    public string? SearchTimestamp { get; init; }
    public int? QueueCount { get; init; }
    public int? CategoryCount { get; init; }
    public string? MetricType { get; init; }
    public WorkerLoopProgress? Torrent { get; init; }
    public WorkerLoopProgress? Search { get; init; }

    public bool Matches(string name, int pid, DateTimeOffset started) =>
        string.Equals(Instance, name, StringComparison.OrdinalIgnoreCase) && Pid == pid && Heartbeat >= started;
    public bool IsStalled(DateTimeOffset now) => Torrent?.IsStalled(now) == true || Search?.IsStalled(now) == true;
    public string DescribeProgress() => $"Torrent: {Torrent?.Phase ?? "Running"}; Search: {Search?.Phase ?? "Running"}";
}
