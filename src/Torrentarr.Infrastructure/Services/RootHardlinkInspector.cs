using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Services;

internal static class RootHardlinkInspector
{
    internal static HardlinkRootInspection Inspect(IReadOnlyList<string> files, IReadOnlyList<string> roots,
        IReadOnlyList<string> ignored, int budget, CancellationToken ct)
    {
        var unknown = files.Distinct(StringComparer.Ordinal).ToDictionary(path => path, _ => (bool?)null);
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8)
            return new(unknown, "Root-aware inspection requires Linux filesystem identity support.");
        if (roots.Count == 0) return new(unknown, "Configure mapped library roots.");
        if (budget < 1) return new(unknown, "Library scan entry budget is exhausted.");
        if (files.Count == 0) return new(unknown, "No inspectable torrent files were selected.");
        var inspected = 0;
        try
        {
            var library = roots.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).Distinct(StringComparer.Ordinal).ToArray();
            var excluded = ignored.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).Distinct(StringComparer.Ordinal).ToArray();
            if (library.Any(root => excluded.Any(ignore => Within(root, ignore))))
                return new(unknown, "An ignored root contains a configured library root.");
            library = library.Where(root => !library.Any(parent => root != parent && Within(root, parent))).ToArray();
            var targets = files.Distinct(StringComparer.Ordinal).ToDictionary(path => path, ReadFile, StringComparer.Ordinal);
            var targetIdentities = targets.GroupBy(target => (target.Value.DeviceMajor, target.Value.DeviceMinor, target.Value.Inode))
                .ToDictionary(group => group.Key, group => group.Select(target => target.Key).ToArray());
            var matches = new HashSet<string>(StringComparer.Ordinal);
            var clock = Stopwatch.StartNew();
            foreach (var root in library)
            {
                using var handle = OpenPath(root, directory: true);
                EnsureSupported(handle);
                var before = Read(handle, "");
                Scan(handle, root, before, 0);
                if (Read(handle, "") != before)
                    throw new IOException("Library root changed during inspection.");
                using var currentRoot = OpenPath(root, directory: true);
                if (Read(currentRoot, "") != before)
                    throw new IOException("Library root identity changed during inspection.");
            }

            foreach (var (path, before) in targets)
                if (ReadFile(path) != before) throw new IOException("Torrent file changed during inspection.");
            return new(targets.Keys.ToDictionary(path => path, path => (bool?)matches.Contains(path)),
                $"Complete scan of {library.Length} library roots and {inspected} entries; matches use filesystem device and inode identity.", inspected);

            void Scan(SafeFileHandle directory, string logicalPath, Metadata parent, int depth)
            {
                if (depth > 64) throw new IOException("Library scan exceeded its directory depth budget.");
                foreach (var entry in Directory.EnumerateFileSystemEntries("/proc/self/fd/" + directory.DangerousGetHandle()))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++inspected > budget || clock.Elapsed > TimeSpan.FromSeconds(30))
                        throw new IOException("Library scan reached its entry or time budget.");
                    var name = Path.GetFileName(entry);
                    var path = Path.Combine(logicalPath, name);
                    if (excluded.Any(ignore => Within(path, ignore))) continue;
                    var metadata = Read(directory, name);
                    if (metadata.DeviceMajor != parent.DeviceMajor || metadata.DeviceMinor != parent.DeviceMinor)
                        throw new IOException("Library scan encountered a nested filesystem mount.");
                    if (metadata.Type == 0x4000)
                    {
                        using var child = OpenAt(directory, name, directory: true);
                        if (Read(child, "") != metadata) throw new IOException("Library directory changed during inspection.");
                        Scan(child, path, metadata, depth + 1);
                        if (Read(child, "") != metadata) throw new IOException("Library directory changed during inspection.");
                    }
                    else if (metadata.Type == 0x8000)
                    {
                        if (targetIdentities.TryGetValue((metadata.DeviceMajor, metadata.DeviceMinor, metadata.Inode), out var targetPaths))
                            foreach (var targetPath in targetPaths)
                                if (Path.GetFullPath(targetPath) != path) matches.Add(targetPath);
                    }
                    else throw new IOException("Library scan encountered a link or unsupported file type.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or EntryPointNotFoundException or DllNotFoundException)
        {
            return new(unknown, ex is IOException ? ex.Message : "Filesystem identity or library coverage is unavailable.", Math.Min(inspected, budget));
        }
    }

    private static bool Within(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + "/";
        return path == root || path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static Metadata ReadFile(string path)
    {
        using var file = OpenPath(path, directory: false);
        EnsureSupported(file);
        var metadata = Read(file, "");
        if (metadata.Type != 0x8000) throw new IOException("Torrent path is not a regular file.");
        return metadata;
    }

    private static SafeFileHandle OpenPath(string path, bool directory)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Inspection requires absolute local paths.");
        var current = new SafeFileHandle((IntPtr)NativeOpenAt(-100, "/", 0x10000 | 0x80000), true);
        try
        {
            var parts = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var next = OpenAt(current, parts[i], i < parts.Length - 1 || directory);
                current.Dispose();
                current = next;
            }
            var result = current;
            current = null!;
            return result;
        }
        finally { current?.Dispose(); }
    }

    private static SafeFileHandle OpenAt(SafeFileHandle parent, string name, bool directory)
    {
        var handle = new SafeFileHandle((IntPtr)NativeOpenAt(parent.DangerousGetHandle().ToInt32(), name,
            0x20000 | 0x80000 | (directory ? 0x10000 : 0x200000)), true);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        throw new IOException("A file or directory is missing, inaccessible, or a symbolic link.");
    }

    private static Metadata Read(SafeFileHandle handle, string name)
    {
        if (Statx(handle, name, name.Length == 0 ? 0x1000 : 0x100, 0x385, out var value) != 0
            || (value.Mask & 0x385) != 0x385 || value.Inode == 0)
            throw new IOException("Filesystem did not provide complete stable identity.");
        return value;
    }

    private static void EnsureSupported(SafeFileHandle handle)
    {
        if (Fstatfs(handle, out var filesystem) != 0
            || filesystem.Type is not (0xef53 or 0x58465342 or 0x9123683e or 0x01021994 or 0x794c7630))
            throw new IOException("Filesystem identity is unsupported; use a local ext4, XFS, Btrfs, tmpfs, or OverlayFS mount.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Metadata : IEquatable<Metadata>
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(96)] public long ChangeSeconds;
        [FieldOffset(104)] public uint ChangeNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        public int Type => Mode & 0xf000;
        public bool Equals(Metadata other) => Inode == other.Inode && DeviceMajor == other.DeviceMajor
            && DeviceMinor == other.DeviceMinor && Size == other.Size && Links == other.Links && Mode == other.Mode
            && ChangeSeconds == other.ChangeSeconds && ChangeNanoseconds == other.ChangeNanoseconds;
        public override bool Equals(object? obj) => obj is Metadata other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(DeviceMajor, DeviceMinor, Inode);
        public static bool operator ==(Metadata left, Metadata right) => left.Equals(right);
        public static bool operator !=(Metadata left, Metadata right) => !left.Equals(right);
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Filesystem { [FieldOffset(0)] public long Type; }

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int NativeOpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(SafeFileHandle directory, string path, int flags, uint mask, out Metadata metadata);
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int Fstatfs(SafeFileHandle handle, out Filesystem filesystem);
}
