using System.Collections.Concurrent;

namespace Torrentarr.Infrastructure.Services;

/// <summary>Last completed allocations, shared between the scoped allocator and WebUI.</summary>
public sealed class FreeSpaceObservations
{
    public sealed record Allocation(long CommittedBytes, long RemainingBytes, DateTimeOffset CompletedAt, string? BlockedReason);
    private readonly ConcurrentDictionary<string, Allocation> _allocations = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string client, string path) => $"{client}\n{path}";
    public Allocation? Get(string client, string path) => _allocations.GetValueOrDefault(Key(client, path));
    public void Set(string client, string path, Allocation allocation) => _allocations[Key(client, path)] = allocation;
}
