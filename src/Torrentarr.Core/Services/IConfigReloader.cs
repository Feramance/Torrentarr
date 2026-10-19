namespace Torrentarr.Core.Services;

public interface IConfigReloader
{
    event EventHandler<ConfigReloadedEventArgs>? ConfigReloaded;

    void StartWatching();

    void StopWatching();

    bool ReloadConfig();

    string ConfigPath { get; }
    long Revision => 0;
    string ResolveInstanceName(string name) => name;
    ConfigurationApplicationStatus GetStatus() => new(0, "applied", new Dictionary<string, ConfigurationComponentStatus>(), null);
    (bool Ok, string? Error) Validate(Torrentarr.Core.Configuration.TorrentarrConfig candidate) => Torrentarr.Core.Configuration.ConfigValidationHelper.ValidateAll(candidate);
    bool Apply(Torrentarr.Core.Configuration.TorrentarrConfig candidate, Dictionary<string, string>? renames = null) => ReloadConfig();
    void Acknowledge(string component, long revision, string? error = null) { }
    Task WaitForChangeAsync(long revision, TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

public class ConfigReloadedEventArgs : EventArgs
{
    public bool Success { get; set; }
    public long Revision { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime ReloadedAt { get; set; } = DateTime.UtcNow;
}
