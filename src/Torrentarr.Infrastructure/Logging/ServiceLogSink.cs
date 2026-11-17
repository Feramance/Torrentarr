using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Torrentarr.Core;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Logging;

/// <summary>Shared daily files for every process, with an aggregate and one service destination.</summary>
public class ServiceLogSink(string logsPath, string processType) : ILogEventSink, IDisposable
{
    internal const long FileSizeLimit = 128L * 1024 * 1024;
    internal const int RetainedFileCount = 8;

    static ServiceLogSink() => Serilog.Debugging.SelfLog.Enable(message => Console.Error.WriteLine(DiagnosticText.Safe(message)));

    private readonly long _fileSizeLimit = FileSizeLimit;
    internal ServiceLogSink(string logsPath, string processType, long fileSizeLimit) : this(logsPath, processType)
        => _fileSizeLimit = fileSizeLimit;

    private readonly Dictionary<string, Logger> _writers = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public static string SafeName(string name)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (safe == name && safe.Length is > 0 and <= 100) return safe;
        return safe[..Math.Min(safe.Length, 100)] + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12];
    }

    public void Emit(LogEvent logEvent)
    {
        var instance = logEvent.Properties.TryGetValue("ProcessInstance", out var property)
            ? (property as ScalarValue)?.Value?.ToString() : InstanceContext.Current;
        if (string.IsNullOrEmpty(instance))
        {
            var match = System.Text.RegularExpressions.Regex.Match(logEvent.RenderMessage(),
                "Worker starting:\\s*\"(?<instance>[^\"]+)\"|Started updating database for \\w+ instance \"(?<instance>[^\"]+)\"|on instance \"(?<instance>[^\"]+)\"|^\\[(?<instance>\\w+)\\]",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking);
            if (match.Success) instance = match.Groups["instance"].Value;
        }
        var source = string.IsNullOrEmpty(instance) ? processType == "WebUI" ? "Torrentarr.WebUI" : "Torrentarr" : $"Torrentarr.{instance}";
        logEvent.AddPropertyIfAbsent(new LogEventProperty("LogSource", new ScalarValue(source)));
        var service = logEvent.RenderMessage().Contains("FreeSpace:", StringComparison.Ordinal) ? "freespace"
            : !string.IsNullOrEmpty(instance) ? "worker-" + SafeName(instance)
            : processType == "WebUI" ? "webui" : "torrentarr";
        lock (_gate)
        {
            Writer("all").Write(logEvent);
            Writer(service).Write(logEvent);
        }
    }

    private Logger Writer(string service)
    {
        if (_writers.TryGetValue(service, out var writer)) return writer;
        Directory.CreateDirectory(logsPath);
        writer = new LoggerConfiguration().MinimumLevel.Verbose()
            .WriteTo.File(new ServiceLogFormatter(), Path.Combine(logsPath, service + "-.log"),
                rollingInterval: RollingInterval.Day, shared: true, retainedFileCountLimit: RetainedFileCount,
                fileSizeLimitBytes: _fileSizeLimit, rollOnFileSizeLimit: true, retainedFileTimeLimit: TimeSpan.FromDays(7))
            .CreateLogger();
        _writers.Add(service, writer);
        return writer;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var writer in _writers.Values) writer.Dispose();
            _writers.Clear();
        }
    }
}

public sealed class ServiceLogFormatter(bool colour = false) : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        var level = logEvent.Level switch
        {
            LogEventLevel.Verbose => "TRACE",
            LogEventLevel.Debug => "DEBUG",
            LogEventLevel.Information => "INFO",
            LogEventLevel.Warning => "WARNING",
            LogEventLevel.Error => "ERROR",
            LogEventLevel.Fatal => "FATAL",
            _ => "INFO"
        };
        var source = logEvent.Properties.TryGetValue("LogSource", out var value) ? (value as ScalarValue)?.Value?.ToString()
            : logEvent.Properties.TryGetValue("ProcessInstance", out value) ? $"Torrentarr.{(value as ScalarValue)?.Value}"
            : logEvent.Properties.TryGetValue("ProcessType", out value) && (value as ScalarValue)?.Value?.ToString() == "WebUI" ? "Torrentarr.WebUI"
            : string.IsNullOrEmpty(InstanceContext.Current) ? "Torrentarr" : $"Torrentarr.{InstanceContext.Current}";
        var code = logEvent.Level switch
        {
            LogEventLevel.Warning => "33",
            LogEventLevel.Error or LogEventLevel.Fatal => "1;31",
            LogEventLevel.Debug or LogEventLevel.Verbose => "35",
            _ => "37"
        };
        if (colour) output.Write("\u001b[32m");
        output.Write($"[{logEvent.Timestamp:yyyy-MM-dd HH:mm:ss}]");
        if (colour) output.Write($"\u001b[0m\u001b[{code}m");
        output.Write($"[{level,-8}]");
        if (colour) output.Write("\u001b[0m\u001b[34m");
        output.Write($"[{DiagnosticText.Safe(source),-20}]");
        if (colour) output.Write($"\u001b[0m\u001b[{code}m");
        output.Write(DiagnosticText.Safe(logEvent.RenderMessage(CultureInfo.InvariantCulture)));
        foreach (var key in new[] { "ProcessId", "SourceContext", "Operation", "CycleId", "ArrInstance", "ClientInstance", "TorrentCategory", "TorrentHash", "MediaId", "CommandId" })
            if (logEvent.Properties.TryGetValue(key, out var context))
                output.Write($" | {key}[{DiagnosticText.Safe(Convert.ToString((context as ScalarValue)?.Value, CultureInfo.InvariantCulture))}]");
        if (colour) output.Write("\u001b[0m");
        output.WriteLine();
        if (logEvent.Exception != null)
        {
            using var lines = new StringReader(logEvent.Exception.ToString());
            while (lines.ReadLine() is { } line) output.WriteLine(DiagnosticText.Safe(line));
        }
    }
}
