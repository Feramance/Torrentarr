using System.Collections.Concurrent;

namespace Torrentarr.Infrastructure.Services;

// Shared across executor scopes so a new search cycle cannot discard an outstanding delay.
public sealed class SearchSubmissionTiming
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    internal SemaphoreSlim Gate(string instance) => _gates.GetOrAdd(instance, _ => new(1, 1));

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSubmissions = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan RemainingDelay(string instance, DateTimeOffset now, TimeSpan interval)
    {
        var remaining = _lastSubmissions.TryGetValue(instance, out var last) ? last + interval - now : TimeSpan.Zero;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public void Record(string instance, DateTimeOffset completedAt) => _lastSubmissions[instance] = completedAt;
}
