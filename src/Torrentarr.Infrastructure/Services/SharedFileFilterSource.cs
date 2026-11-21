using System.Globalization;
using Torrentarr.Core.Configuration;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.RegularExpressions;

namespace Torrentarr.Infrastructure.Services;

internal static class SharedFileFilterSource
{
    internal const int MaxBytes = 32 * 1024;
    internal const int MaxPatterns = 128;
    internal const int MaxPathLength = 4096;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = ConnectAsync
    });

    internal static async Task<string> ReadAsync(string source, string directory, CancellationToken ct)
    {
        if (ConfigValidationHelper.SharedFileFilterSourceError(source) is { } error)
            throw new FormatException(error);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new FormatException("Shared list exceeds the byte limit.");
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await ReadBoundedAsync(body, timeout.Token);
        }

        var path = ResolveLocalPath(source, directory);
        if (!File.Exists(path))
            throw new FileNotFoundException("Shared list file is unavailable.");
        // Only regular files may be read. In particular, opening a FIFO could block before any timeout applies.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            using var handle = OpenUnixFile(directory, Path.GetRelativePath(directory, path));
            await using var stream = new FileStream(handle, FileAccess.Read);
            return await ReadBoundedAsync(stream, timeout.Token);
        }

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await ReadBoundedAsync(file, timeout.Token);
    }

    private static SafeFileHandle OpenUnixFile(string directory, string relativePath)
    {
        var mac = OperatingSystem.IsMacOS();
        var noFollow = mac ? 0x100 : 0x20000;
        var directoryOnly = mac ? 0x100000 : 0x10000;
        var closeOnExec = mac ? 0x1000000 : 0x80000;
        var nonBlocking = mac ? 0x4 : 0x800;
        var handle = new SafeFileHandle((IntPtr)OpenAt(mac ? -2 : -100, directory, directoryOnly | closeOnExec), ownsHandle: true);
        try
        {
            var segments = relativePath.Split(Path.DirectorySeparatorChar);
            for (var i = 0; i < segments.Length; i++)
            {
                if (handle.IsInvalid) throw new IOException("Cannot open shared list directory.");
                var flags = noFollow | closeOnExec | (i == segments.Length - 1 ? nonBlocking : directoryOnly);
                var next = new SafeFileHandle((IntPtr)OpenAt(handle.DangerousGetHandle().ToInt32(), segments[i], flags), ownsHandle: true);
                handle.Dispose();
                handle = next;
            }

            if (handle.IsInvalid) throw new IOException("Cannot open shared list file.");
            var regular = mac
                ? Fstat(handle, out var macMetadata) == 0 && (macMetadata.Mode & 0xf000) == 0x8000
                : Statx(handle, "", 0x1000, 1, out var metadata) == 0
                    && (metadata.Mask & 1) != 0 && (metadata.Mode & 0xf000) == 0x8000;
            if (!regular) throw new IOException("Shared list source must be a regular file.");
            var result = handle;
            handle = null!;
            return result;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileMetadata
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct MacFileMetadata
    {
        [FieldOffset(4)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(SafeFileHandle handle, out MacFileMetadata metadata);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directoryHandle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(SafeFileHandle handle, string path, int flags, uint mask, out LinuxFileMetadata metadata);

    internal static string ResolveLocalPath(string source, string directory)
    {
        var segments = source.Split(['/', '\\']);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':')))
            throw new FormatException("Use a relative list path below the Torrentarr data directory.");

        var path = Path.GetFullPath(directory);
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new FormatException("Shared list paths cannot traverse links or devices.");
        }

        return path;
    }

    internal static Regex[] Compile(string content, bool caseSensitive)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxBytes)
            throw new FormatException("Shared list exceeds the byte limit.");

        using var lines = new StringReader(content.TrimStart('\uFEFF'));
        var patterns = new List<Regex>();
        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;
        while (lines.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (patterns.Count == MaxPatterns || line.Length > 256 || line.Any(char.IsControl))
                throw new FormatException("Shared list exceeds a pattern limit or contains control characters.");
            try
            {
                patterns.Add(new Regex(line, options, TimeSpan.FromMilliseconds(25)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                throw new FormatException($"Invalid or unsupported shared regex at pattern {(patterns.Count + 1).ToString(CultureInfo.InvariantCulture)}.");
            }
        }

        return patterns.ToArray();
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new byte[MaxBytes + 1];
        var size = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, ct);
        if (size > MaxBytes) throw new FormatException("Shared list exceeds the byte limit.");
        return new UTF8Encoding(false, true).GetString(bytes, 0, size);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] is > 0 and < 224 and not (10 or 127)
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && (bytes[1] is 0 or 168 || (bytes[1] == 88 && bytes[2] == 99)))
                && !(bytes[0] == 198 && (bytes[1] is 18 or 19 || (bytes[1] == 51 && bytes[2] == 100)))
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);

        // Restrict IPv6 to global unicast, excluding transition and special-purpose blocks.
        return (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] < 2 || (bytes[2] == 0x0d && bytes[3] == 0xb8)))
            && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x3f && bytes[1] == 0xff);
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("Shared list host must resolve exclusively to public addresses.");

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
                ct.ThrowIfCancellationRequested();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("Could not connect to the shared list host.");
    }
}
