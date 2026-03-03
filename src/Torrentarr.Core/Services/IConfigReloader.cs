namespace Torrentarr.Core.Services;

public interface IConfigReloader
{
    event EventHandler<ConfigReloadedEventArgs>? ConfigReloaded;

    void StartWatching();

    void StopWatching();

    bool ReloadConfig();

    string ConfigPath { get; }
}

public class ConfigReloadedEventArgs : EventArgs
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime ReloadedAt { get; set; } = DateTime.UtcNow;
}
