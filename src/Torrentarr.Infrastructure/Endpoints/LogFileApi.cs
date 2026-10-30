using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Torrentarr.Infrastructure.Endpoints;

/// <summary>qBitrr log search / SSE helpers shared by Host and WebUI.</summary>
public static class LogFileApi
{
    public sealed record TailPayload(string content, long next_bytes, long size, long inode, bool rotated, bool truncated, long start_bytes = 0, long start_inode = 0, long start_line = 1);
    private const int MaxWindowBytes = 8 * 1024 * 1024;

    public static async Task<IResult> TailFromRequestAsync(string logsRoot, string name, HttpRequest request, CancellationToken ct)
    {
        var path = ResolveLogFile(logsRoot, name);
        if (path is null) return Results.BadRequest(new { error = "Invalid log file name" });
        try
        {
            var payload = await ReadWindowAsync(path, request, ct);
            return Results.Ok(payload);
        }
        catch (FileNotFoundException) { return Results.NotFound(new { error = "Log file not found" }); }
        catch (DirectoryNotFoundException) { return Results.NotFound(new { error = "Log file not found" }); }
    }

    public static async Task<TailPayload> ReadWindowAsync(string path, HttpRequest request, CancellationToken ct)
    {
        static long Number(HttpRequest req, string key, long fallback = 0) =>
            long.TryParse(req.Query[key], out var value) ? Math.Max(0, value) : fallback;
        var lines = (int)Math.Clamp(Number(request, "lines", 2000), 1, 20000);
        var offset = (int)Math.Min(Number(request, "offset"), 100000);
        var around = Number(request, "around_line");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = stream.Length;
        var identity = await CursorFingerprintAsync(stream, size, ct);
        var since = Number(request, "since_bytes");
        var generation = Number(request, "inode");
        var delta = request.Query.ContainsKey("since_bytes") && !request.Query.ContainsKey("before_bytes");
        var rotated = delta && (since > size || (generation != 0 && generation != await CursorFingerprintAsync(stream, since, ct)));

        var history = request.Query.ContainsKey("before_bytes");
        var before = Number(request, "before_bytes", size);
        var historyAnchor = request.Query.ContainsKey("since_bytes") ? since : before;
        if (history && (before > size || historyAnchor > size || (generation != 0 && generation != await CursorFingerprintAsync(stream, historyAnchor, ct))))
        {
            rotated = true;
            before = size;
        }
        long windowStart = 0;
        long windowEnd = history ? before : size;
        var startLine = Math.Max(1, around - lines / 2);
        if (around > 0)
        {
            // Locate actual byte boundaries; StreamReader.Position includes read-ahead bytes.
            var buffer = new byte[65536];
            long position = 0, line = 1;
            windowEnd = 0;
            stream.Position = 0;
            while (position < Math.Min(size, SearchMaxBytes))
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, Math.Min(size, SearchMaxBytes) - position)), ct);
                if (count == 0) break;
                var done = false;
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    var boundary = position + i + 1;
                    if (line < startLine) windowStart = boundary;
                    else windowEnd = boundary;
                    if (++line >= startLine + lines) { done = true; break; }
                }
                position += count;
                if (done) break;
            }
            if (windowEnd < windowStart) windowEnd = windowStart;
        }
        var start = around > 0 ? windowStart
            : delta && !rotated ? since : Math.Max(0, windowEnd - MaxWindowBytes);
        var length = (int)Math.Min(windowEnd - start, MaxWindowBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var bytes = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(read, length - read), ct);
            if (count == 0) break;
            read += count;
        }
        // Delta cursors stop at a complete line, so partial writes and UTF-8 characters are retried.
        var complete = read == 0 ? -1 : Array.LastIndexOf(bytes, (byte)'\n', read - 1, read);
        if (delta && !rotated)
        {
            var consumed = complete + 1;
            return new(System.Text.Encoding.UTF8.GetString(bytes, 0, consumed), start + consumed, size, await CursorFingerprintAsync(stream, start + consumed, ct), false, start + consumed < size);
        }
        var boundaries = new List<int> { 0 };
        for (var i = 0; i <= complete; i++)
            if (bytes[i] == (byte)'\n') boundaries.Add(i + 1);
        var first = 0;
        if (start > 0)
        {
            stream.Position = start - 1;
            if (stream.ReadByte() != (byte)'\n') first = Math.Min(1, boundaries.Count - 1);
        }
        var end = Math.Max(first, boundaries.Count - 1 - (around > 0 || history ? 0 : offset));
        first = Math.Max(first, end - lines);
        var startBytes = start + boundaries[first];
        var cursor = start + complete + 1;
        var text = System.Text.Encoding.UTF8.GetString(bytes, boundaries[first], boundaries[end] - boundaries[first]).TrimEnd('\n');
        return new(text, cursor, size, await CursorFingerprintAsync(stream, cursor, ct), rotated,
            startBytes > 0 || cursor < size, startBytes, await CursorFingerprintAsync(stream, startBytes, ct), around > 0 ? startLine : 1);
    }

    // Fingerprint both ends of the consumed window: stable on append, detects replacement and copy-truncate.
    private static async Task<long> CursorFingerprintAsync(FileStream stream, long boundary, CancellationToken ct)
    {
        var first = (int)Math.Min(512, boundary);
        var last = (int)Math.Min(512, boundary - first);
        var bytes = new byte[first + last];
        stream.Position = 0;
        await stream.ReadExactlyAsync(bytes.AsMemory(0, first), ct);
        stream.Position = boundary - last;
        await stream.ReadExactlyAsync(bytes.AsMemory(first, last), ct);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return (long)(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash) & ((1UL << 53) - 1));
    }

    public const int SearchMaxMatchesDefault = 200;
    public const int SearchMaxMatchesHard = 1000;
    public const int SearchContextHard = 10;
    public const int RegexPatternMax = 256;
    public const long SearchMaxBytes = 80L * 1024 * 1024;
    public const double SearchMaxSeconds = 8.0;
    public const double SsePollSeconds = 0.75;
    public const double SsePingSeconds = 15.0;
    public const double SseMaxSeconds = 120.0;


    /// <summary>
    /// Make a stable, on-disk snapshot of a log that may be appended or rotated while
    /// being downloaded. DeleteOnClose prevents lingering temporary files.
    /// </summary>
    public static async Task<Stream> CreateDownloadSnapshotAsync(string path, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"torrentarr-download-{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            BufferSize = 65536,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
        };
        // The source log may contain secrets. Restrict the snapshot from its first byte.
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var snapshot = new FileStream(tempPath, options);
        try
        {
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous))
                await source.CopyToAsync(snapshot, ct);
            snapshot.Position = 0;
            return snapshot;
        }
        catch
        {
            await snapshot.DisposeAsync();
            throw;
        }
    }

    public static bool IsValidLogFileName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Contains('/')
        && !name.Contains('\\')
        && !name.Contains("..")
        && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

    public static string? ResolveLogFile(string logsRoot, string name)
    {
        if (!IsValidLogFileName(name))
            return null;
        var root = Path.GetFullPath(logsRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, Path.GetFileName(name)));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(candidate, root, StringComparison.Ordinal))
            return null;
        return candidate;
    }

    public static IResult SearchFromRequest(string logsRoot, string name, HttpRequest request)
    {
        int? maxMatches = int.TryParse(request.Query["max_matches"].FirstOrDefault(), out var mm) ? mm : null;
        int? context = int.TryParse(request.Query["context"].FirstOrDefault(), out var ctx) ? ctx : null;
        return Search(
            logsRoot,
            name,
            request.Query["q"].FirstOrDefault(),
            request.Query["case"].FirstOrDefault(),
            request.Query["regex"].FirstOrDefault(),
            request.Query["include_rotated"].FirstOrDefault(),
            maxMatches,
            context);
    }

    public static IResult Search(
        string logsRoot,
        string name,
        string? query,
        string? caseFlag,
        string? regexFlag,
        string? includeRotatedFlag,
        int? maxMatches,
        int? context)
    {
        var q = (query ?? "").Trim();
        if (string.IsNullOrEmpty(q))
            return Results.BadRequest(new { error = "query is required" });

        var caseSensitive = caseFlag is "1" or "true" or "True";
        var useRegex = regexFlag is "1" or "true" or "True";
        var includeRotated = includeRotatedFlag is not ("0" or "false" or "False");
        var matchCap = Math.Clamp(maxMatches ?? SearchMaxMatchesDefault, 1, SearchMaxMatchesHard);
        var contextN = Math.Clamp(context ?? 2, 0, SearchContextHard);

        Func<string, bool>? matcher;
        try
        {
            matcher = CompileMatcher(q, caseSensitive, useRegex);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var files = SiblingLogFiles(logsRoot, name, includeRotated);
        if (files.Count == 0)
            return Results.NotFound(new { error = "not found" });

        var (matches, truncated) = SearchFiles(files, matcher, matchCap, contextN);
        return Results.Ok(new
        {
            query = q,
            truncated,
            matches,
            files_searched = files.Select(f => Path.GetFileName(f)).ToList()
        });
    }

    public static async Task StreamAsync(HttpContext ctx, string logsRoot, string name, CancellationToken ct)
    {
        var path = ResolveLogFile(logsRoot, name);
        if (path == null || !File.Exists(path))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = "not found" }, ct);
            return;
        }

        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Response.Headers.Connection = "keep-alive";

        if (ctx.Request.Query["format"] == "json")
        {
            var startedAt = DateTime.UtcNow;
            TailPayload? latest = null;
            while (!ct.IsCancellationRequested && (DateTime.UtcNow - startedAt).TotalSeconds < SseMaxSeconds)
            {
                try
                {
                    var payload = await ReadWindowAsync(path, ctx.Request, ct);
                    latest = payload;
                    await ctx.Response.WriteAsync($"event: {(payload.rotated ? "rotated" : "append")}\ndata: {System.Text.Json.JsonSerializer.Serialize(payload)}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(ctx.Request.QueryString.Value ?? "");
                    query["since_bytes"] = payload.next_bytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    query["inode"] = payload.inode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    ctx.Request.Query = new QueryCollection(query);
                    await Task.Delay(TimeSpan.FromSeconds(SsePollSeconds), ct);
                }
                catch (FileNotFoundException) { await Task.Delay(TimeSpan.FromSeconds(SsePollSeconds), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
            if (!ct.IsCancellationRequested && latest is not null)
            {
                await ctx.Response.WriteAsync($"event: reconnect\ndata: {System.Text.Json.JsonSerializer.Serialize(latest)}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
            return;
        }

        var started = DateTime.UtcNow;
        var lastPing = DateTime.UtcNow;
        long lastLength = 0;
        try
        {
            lastLength = new FileInfo(path).Length;
        }
        catch
        {
            lastLength = 0;
        }

        while (!ct.IsCancellationRequested)
        {
            if ((DateTime.UtcNow - started).TotalSeconds >= SseMaxSeconds)
                break;

            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > lastLength)
                {
                    await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(lastLength, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs);
                    var chunk = await reader.ReadToEndAsync(ct);
                    lastLength = fs.Length;
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        foreach (var line in chunk.Replace("\r\n", "\n").Split('\n'))
                        {
                            await ctx.Response.WriteAsync($"data: {line}\n\n", ct);
                        }
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // keep the stream alive across rotations / brief IO errors
            }

            if ((DateTime.UtcNow - lastPing).TotalSeconds >= SsePingSeconds)
            {
                await ctx.Response.WriteAsync(": ping\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                lastPing = DateTime.UtcNow;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(SsePollSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static Func<string, bool> CompileMatcher(string query, bool caseSensitive, bool useRegex)
    {
        if (useRegex)
        {
            if (query.Length > RegexPatternMax)
                throw new ArgumentException("regex pattern too long");
            var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            Regex pattern;
            try
            {
                pattern = new Regex(query, options, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"invalid regex: {ex.Message}");
            }
            return line => pattern.IsMatch(line);
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return line => line.Contains(query, comparison);
    }

    private static List<string> SiblingLogFiles(string logsRoot, string name, bool includeRotated)
    {
        var files = new List<string>();
        var primary = ResolveLogFile(logsRoot, name);
        if (primary != null && File.Exists(primary))
            files.Add(primary);
        if (!includeRotated || !Directory.Exists(logsRoot))
            return files;

        var prefix = Path.GetFileName(name);
        foreach (var candidate in Directory.GetFiles(logsRoot, prefix + "*"))
        {
            if (files.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                continue;
            files.Add(candidate);
        }

        return files
            .OrderBy(f => Path.GetFileName(f) == Path.GetFileName(name) ? 0 : 1)
            .ThenByDescending(f => File.GetLastWriteTimeUtc(f))
            .ToList();
    }

    private static (List<object> Matches, bool Truncated) SearchFiles(
        List<string> files,
        Func<string, bool> matcher,
        int maxMatches,
        int context)
    {
        var matches = new List<object>();
        var truncated = false;
        long bytesScanned = 0;
        var started = DateTime.UtcNow;

        foreach (var path in files)
        {
            if (matches.Count >= maxMatches || bytesScanned >= SearchMaxBytes
                || (DateTime.UtcNow - started).TotalSeconds >= SearchMaxSeconds)
            {
                truncated = true;
                break;
            }

            var before = new Queue<string>();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                var lineNo = 0;
                string? raw;
                while ((raw = reader.ReadLine()) != null)
                {
                    lineNo++;
                    bytesScanned += raw.Length;
                    if (bytesScanned >= SearchMaxBytes
                        || (DateTime.UtcNow - started).TotalSeconds >= SearchMaxSeconds)
                    {
                        truncated = true;
                        break;
                    }

                    if (matcher(raw))
                    {
                        if (matches.Count >= maxMatches)
                        {
                            truncated = true;
                            break;
                        }

                        matches.Add(new
                        {
                            file = Path.GetFileName(path),
                            line = lineNo,
                            text = raw,
                            context_before = before.ToList(),
                            context_after = Array.Empty<string>()
                        });
                    }

                    if (context > 0)
                    {
                        before.Enqueue(raw);
                        while (before.Count > context)
                            before.Dequeue();
                    }
                }
            }
            catch
            {
                // skip unreadable files
            }
        }

        return (matches, truncated);
    }
}
