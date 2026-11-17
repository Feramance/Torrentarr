namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// qBitrr <c>database_restart_event</c> parity: signal coordinated worker restart after persistent DB errors.
/// </summary>
public class DatabaseRestartCoordinator
{
    private readonly object _lock = new();
    private DateTime _firstErrorTime;
    internal TimeProvider Clock { get; set; } = TimeProvider.System;
    private DateTime _lastErrorTime;
    private volatile bool _restartRequested;
    private long _restartVersion;

    public bool RestartRequested => _restartRequested;

    public (bool Requested, long Version) GetRestartRequest()
    {
        lock (_lock) return (_restartRequested, _restartVersion);
    }

    public void RecordDatabaseError()
    {
        lock (_lock)
        {
            var now = Clock.GetUtcNow().UtcDateTime;
            if (now - _lastErrorTime > TimeSpan.FromMinutes(5))
                _firstErrorTime = now;
            _lastErrorTime = now;

            if (now - _firstErrorTime > TimeSpan.FromMinutes(5))
            {
                _restartRequested = true;
                Interlocked.Increment(ref _restartVersion);
            }
        }
    }

    public void RecordDatabaseSuccess()
    {
        lock (_lock)
        {
            _lastErrorTime = default;
        }
    }

    public void ClearRestartRequest(long version)
    {
        lock (_lock)
        {
            if (_restartVersion == version)
                _restartRequested = false;
        }
    }
}
