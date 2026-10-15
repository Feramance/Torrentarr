using System.Collections.Concurrent;

namespace Torrentarr.Core.Configuration;

public sealed class ConfigurationBusyException(string message) : InvalidOperationException(message);

public static class ConfigMutationGuard
{
    private sealed class State { public int Readers; public bool Writing; public bool CoordinateProcesses; public object Gate { get; } = new(); }
    private sealed class Lease(Action release, string path, bool writing) : IDisposable
    {
        public string Path { get; } = path;
        public bool Writing { get; } = writing;
        public bool Disposed { get; private set; }
        public void Dispose() { if (Disposed) return; Disposed = true; release(); }
    }
    private static readonly ConcurrentDictionary<string, State> States = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly AsyncLocal<Lease?> Owner = new();

    public static void EnableProcessCoordination(string path)
    {
        var state = States.GetOrAdd(Path.GetFullPath(path), _ => new());
        lock (state.Gate) state.CoordinateProcesses = true;
    }

    public static bool OwnsMutation(string path) => Owner.Value is { Disposed: false, Writing: true } owner
        && owner.Path == Path.GetFullPath(path);

    public static IDisposable EnterProcessing(string? path = null) => Enter(path, false, false);
    public static IDisposable EnterMutation(string path, bool recovery = false) => Enter(path, true, recovery);

    private static IDisposable Enter(string? path, bool writing, bool recovery)
    {
        path = Path.GetFullPath(path ?? ConfigurationLoader.GetDefaultConfigPath());
        var ownsMutation = Owner.Value is { Disposed: false } owner && owner.Path == path && owner.Writing;
        if (writing && ownsMutation) return new Lease(() => { }, path, false);
        var state = States.GetOrAdd(path, _ => new());
        FileStream? fileLock = null;
        lock (state.Gate)
        {
            if (state.Writing && !ownsMutation || writing && state.Readers > 0)
                throw new ConfigurationBusyException("Configuration is busy. Retry when processing completes.");
            if (!recovery && !ownsMutation && File.Exists(path + ".rename.json"))
                throw new ConfigurationBusyException("An instance rename requires recovery before processing or configuration changes.");
            if (writing || state.CoordinateProcesses && !ownsMutation)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                try
                {
                    fileLock = new FileStream(path + ".write-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    writing ? FileShare.None : FileShare.ReadWrite);
                }
                catch (IOException) { throw new ConfigurationBusyException("Another process is using configuration."); }
            }
            if (writing) state.Writing = true;
            else state.Readers++;
        }
        var previous = Owner.Value;
        var lease = new Lease(() =>
        {
            lock (state.Gate)
            {
                if (writing) state.Writing = false;
                else state.Readers--;
                fileLock?.Dispose();
            }
            Owner.Value = previous;
        }, path, writing);
        if (writing) Owner.Value = lease;
        return lease;
    }

    public static void WriteAtomic(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                using var writer = new StreamWriter(stream, leaveOpen: true);
                writer.Write(text);
                writer.Flush();
                stream.Flush(true);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, File.Exists(path) ? File.GetUnixFileMode(path)
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (OperatingSystem.IsWindows() && File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
