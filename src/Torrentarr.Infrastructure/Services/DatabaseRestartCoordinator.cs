namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// qBitrr <c>database_restart_event</c> parity: signal coordinated worker restart after persistent DB errors.
/// </summary>
public class DatabaseRestartCoordinator
{
    private readonly object _lock = new();
    private int _errorCount;
    private DateTime _firstErrorTime;
    private DateTime _lastErrorTime;
    private volatile bool _restartRequested;
    private long _restartVersion;

    public bool RestartRequested => _restartRequested;

    public (bool Requested, long Version) GetRestartRequest() => (_restartRequested, Interlocked.Read(ref _restartVersion));

    public void RecordDatabaseError()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastErrorTime > TimeSpan.FromMinutes(5))
            {
                _errorCount = 0;
                _firstErrorTime = now;
            }

            _errorCount++;
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
            _errorCount = 0;
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
