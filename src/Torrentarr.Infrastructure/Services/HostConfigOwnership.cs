using Torrentarr.Core.Configuration;

namespace Torrentarr.Infrastructure.Services;

public static class HostConfigOwnership
{
    public static IDisposable Acquire(string configPath)
    {
        var path = Path.Combine(ConfigurationLoader.GetDataDirectoryPath(configPath), ".torrentarr-host.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new ConfigurationBusyException("A Host already manages this data directory. Use its WebUI to change configuration."); }
    }

    public static void RequireStandaloneOwnership(string configPath)
    {
        using var probe = Acquire(configPath);
    }
}
