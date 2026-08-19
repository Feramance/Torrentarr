using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Torrentarr.Infrastructure.Endpoints;

/// <summary>qBitrr log search / SSE helpers shared by Host and WebUI.</summary>
public static class LogFileApi
{
    public const int SearchMaxMatchesDefault = 200;
    public const int SearchMaxMatchesHard = 1000;
    public const int SearchContextHard = 10;
    public const int RegexPatternMax = 256;
    public const long SearchMaxBytes = 80L * 1024 * 1024;
    public const double SearchMaxSeconds = 8.0;
    public const double SsePollSeconds = 0.75;
    public const double SsePingSeconds = 15.0;
    public const double SseMaxSeconds = 120.0;

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
