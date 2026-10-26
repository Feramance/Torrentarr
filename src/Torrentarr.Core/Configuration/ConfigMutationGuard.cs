using System.Collections.Concurrent;

namespace Torrentarr.Core.Configuration;

public sealed class ConfigurationBusyException(string message) : InvalidOperationException(message);

public static class ConfigMutationGuard
{
    private enum LeaseKind { Processing, Publication, Mutation }
    private sealed class State { public int Readers; public bool Publishing; public bool Writing; public bool CoordinateProcesses; public object Gate { get; } = new(); }
    private sealed class Lease(Action release, string path, LeaseKind kind) : IDisposable
    {
        public string Path { get; } = path;
        public LeaseKind Kind { get; } = kind;
        public bool Disposed { get; private set; }
        public void Dispose() { if (Disposed) return; Disposed = true; release(); }
    }
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, State> States = new(Paths);
    private static readonly AsyncLocal<Lease?> Owner = new();

    public static void EnableProcessCoordination(string path)
    {
        var state = States.GetOrAdd(Path.GetFullPath(path), _ => new());
        lock (state.Gate) state.CoordinateProcesses = true;
    }

    public static bool OwnsMutation(string path) => Owner.Value is { Disposed: false, Kind: LeaseKind.Mutation } owner
        && Paths.Equals(owner.Path, Path.GetFullPath(path));

    public static bool OwnsPublication(string path) => Owner.Value is { Disposed: false } owner
        && owner.Kind != LeaseKind.Processing && Paths.Equals(owner.Path, Path.GetFullPath(path));

    public static IDisposable EnterProcessing(string? path = null) => Enter(path, LeaseKind.Processing, false);
    public static IDisposable EnterPublication(string path) => Enter(path, LeaseKind.Publication, false);
    public static IDisposable EnterMutation(string path, bool recovery = false) => Enter(path, LeaseKind.Mutation, recovery);

    private static IDisposable Enter(string? path, LeaseKind kind, bool recovery)
    {
        path = Path.GetFullPath(path ?? ConfigurationLoader.GetDefaultConfigPath());
        var ownsMutation = OwnsMutation(path);
        var ownsPublication = OwnsPublication(path);
        if (kind == LeaseKind.Publication && ownsPublication || kind == LeaseKind.Mutation && ownsMutation)
            return new Lease(() => { }, path, kind);
        var state = States.GetOrAdd(path, _ => new());
        FileStream? processingLock = null;
        FileStream? publicationLock = null;
        lock (state.Gate)
        {
            if (state.Writing && !ownsMutation
                || kind == LeaseKind.Mutation && (state.Readers > 0 || state.Publishing)
                || kind == LeaseKind.Publication && state.Publishing)
                throw new ConfigurationBusyException("Configuration is busy. Retry after the current configuration change or exclusive operation completes.");
            try
            {
                if (kind != LeaseKind.Processing || state.CoordinateProcesses && !ownsPublication)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // Publications share the processing barrier; renames exclude every reader and publisher.
                    processingLock = new FileStream(path + ".write-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        kind == LeaseKind.Mutation ? FileShare.None : FileShare.ReadWrite);
                    if (kind != LeaseKind.Processing)
                        publicationLock = new FileStream(path + ".publish-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                // Check after taking the barrier so a crashed rename cannot leave a journal between the check and acquisition.
                if (!recovery && !ownsPublication && File.Exists(path + ".rename.json"))
                    throw new ConfigurationBusyException("An instance rename requires recovery before processing or configuration changes.");
            }
            catch (IOException)
            {
                publicationLock?.Dispose();
                processingLock?.Dispose();
                throw new ConfigurationBusyException("Another process is changing configuration or performing an exclusive operation.");
            }
            catch
            {
                publicationLock?.Dispose();
                processingLock?.Dispose();
                throw;
            }
            if (kind == LeaseKind.Mutation) state.Writing = true;
            else if (kind == LeaseKind.Publication) state.Publishing = true;
            else state.Readers++;
        }
        var previous = Owner.Value;
        var lease = new Lease(() =>
        {
            lock (state.Gate)
            {
                if (kind == LeaseKind.Mutation) state.Writing = false;
                else if (kind == LeaseKind.Publication) state.Publishing = false;
                else state.Readers--;
                publicationLock?.Dispose();
                processingLock?.Dispose();
            }
            if (kind != LeaseKind.Processing) Owner.Value = previous;
        }, path, kind);
        if (kind != LeaseKind.Processing) Owner.Value = lease;
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
