using System.Collections.Concurrent;

namespace Torrentarr.Infrastructure.Services;

/// <summary>Per-IP rate limiter for login endpoint: 10 attempts per 15 minutes.</summary>
public static class LoginRateLimiter
{
    private const int WindowMinutes = 15;
    private const int MaxAttempts = 10;
    private const int CleanupThreshold = 200;
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> Attempts = new();
    private static readonly object Lock = new();

    public static bool TryAcquire(string key)
    {
        var now = DateTime.UtcNow;
        var window = TimeSpan.FromMinutes(WindowMinutes);
        lock (Lock)
        {
            if (Attempts.Count >= CleanupThreshold)
            {
                var toRemove = Attempts.Where(kvp => now - kvp.Value.WindowStart > window).Select(kvp => kvp.Key).ToList();
                foreach (var k in toRemove)
                    Attempts.TryRemove(k, out _);
            }
            if (Attempts.TryGetValue(key, out var v))
            {
                if (now - v.WindowStart > window)
                    Attempts[key] = (1, now);
                else if (v.Count >= MaxAttempts)
                    return false;
                else
                    Attempts[key] = (v.Count + 1, v.WindowStart);
            }
            else
                Attempts[key] = (1, now);
            return true;
        }
    }
}
