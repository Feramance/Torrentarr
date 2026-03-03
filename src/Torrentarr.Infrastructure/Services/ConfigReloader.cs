using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Infrastructure.Services;

public class ConfigReloader : IConfigReloader, IDisposable
{
    private readonly ILogger<ConfigReloader> _logger;
    private readonly ConfigurationLoader _loader;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _lock = new();
    private DateTime _lastReloadTime = DateTime.MinValue;
    private readonly TimeSpan _debounceTime = TimeSpan.FromSeconds(1);

    public event EventHandler<ConfigReloadedEventArgs>? ConfigReloaded;

    public string ConfigPath { get; }

    public ConfigReloader(ILogger<ConfigReloader> logger)
    {
        _logger = logger;
        _loader = new ConfigurationLoader();

        var configPath = Environment.GetEnvironmentVariable("TORRENTARR_CONFIG");
        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
        {
            ConfigPath = configPath;
        }
        else
        {
            var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var possiblePaths = new[]
            {
                Path.Combine(homePath, "config", "config.toml"),
                Path.Combine(homePath, ".config", "qbitrr", "config.toml"),
                Path.Combine(homePath, ".config", "torrentarr", "config.toml"),
                Path.Combine(".", "config.toml"),
                Path.Combine(".", "config", "config.toml")
            };

            ConfigPath = possiblePaths.FirstOrDefault(File.Exists) ?? possiblePaths[0];
        }

        var directory = Path.GetDirectoryName(ConfigPath);
        var fileName = Path.GetFileName(ConfigPath);

        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            _watcher = new FileSystemWatcher(directory)
            {
                Filter = fileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = false
            };

            _watcher.Changed += OnConfigFileChanged;
        }
    }

    public void StartWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = true;
            _logger.LogInformation("ConfigReloader: watching for changes to {Path}", ConfigPath);
        }
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _logger.LogTrace("ConfigReloader: stopped watching for changes");
        }
    }

    public bool ReloadConfig()
    {
        lock (_lock)
        {
            try
            {
                _logger.LogInformation("ConfigReloader: reloading configuration from {Path}", ConfigPath);

                var newConfig = _loader.Load();

                var args = new ConfigReloadedEventArgs
                {
                    Success = true,
                    ReloadedAt = DateTime.UtcNow
                };

                ConfigReloaded?.Invoke(this, args);

                _logger.LogInformation("ConfigReloader: configuration reloaded successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConfigReloader: failed to reload configuration");

                var args = new ConfigReloadedEventArgs
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ReloadedAt = DateTime.UtcNow
                };

                ConfigReloaded?.Invoke(this, args);

                return false;
            }
        }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now - _lastReloadTime < _debounceTime)
        {
            _logger.LogTrace("ConfigReloader: debouncing config change event");
            return;
        }

        _lastReloadTime = now;
        _logger.LogInformation("ConfigReloader: detected change in {Path}", e.FullPath);

        Task.Run(() =>
        {
            try
            {
                Thread.Sleep(100);
                ReloadConfig();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConfigReloader: error processing config change");
            }
        });
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
