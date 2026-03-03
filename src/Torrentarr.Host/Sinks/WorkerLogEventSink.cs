using Serilog.Core;
using Serilog.Events;
using Torrentarr.Core;

namespace Torrentarr.Host.Sinks;

public class WorkerLogEventSink : ILogEventSink
{
    private readonly string _logsPath;
    private readonly Dictionary<string, StreamWriter> _writers = new();
    private readonly object _lock = new();
    private readonly DateTime _date;

    public WorkerLogEventSink(string logsPath)
    {
        _logsPath = logsPath;
        _date = DateTime.Now.Date;
        
        // Rotate existing log files to .old on startup
        if (Directory.Exists(_logsPath))
        {
            var todayPattern = $"*-{_date:yyyyMMdd}.log";
            foreach (var existingFile in Directory.GetFiles(_logsPath, todayPattern))
            {
                try 
                {
                    var oldPath = existingFile.Replace(".log", ".old");
                    if (File.Exists(oldPath)) 
                        File.Delete(oldPath);
                    File.Move(existingFile, oldPath);
                } 
                catch { /* ignore */ }
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        string? instance = null;
        var message = logEvent.RenderMessage();
        
        // First try to get from ProcessInstance property
        if (logEvent.Properties.TryGetValue("ProcessInstance", out var prop))
        {
            if (prop is ScalarValue scalar && scalar.Value is string s)
                instance = s;
            else if (prop is ScalarValue sv && sv.Value != null)
                instance = sv.Value.ToString();
        }

        // Fallback: check InstanceContext.Current (AsyncLocal)
        if (string.IsNullOrEmpty(instance))
        {
            instance = InstanceContext.Current;
        }

        // Fallback: extract instance name from log message if not in property
        if (string.IsNullOrEmpty(instance))
        {            
            // Match: Worker starting: "Sonarr-Anime"
            var match = System.Text.RegularExpressions.Regex.Match(
                message, @"Worker starting:\s*""([^""]+)""");
            if (match.Success)
            {
                instance = match.Groups[1].Value.Trim('"');
            }
            
            // Match: Started updating database for Radarr instance "Radarr-1080"
            if (string.IsNullOrEmpty(instance))
            {
                match = System.Text.RegularExpressions.Regex.Match(
                    message, @"Started updating database for \w+ instance ""([^""]+)""");
                if (match.Success)
                {
                    instance = match.Groups[1].Value.Trim('"');
                }
            }
            
            // Match: Processing torrents for category "radarr" on instance "Radarr-1080"  
            if (string.IsNullOrEmpty(instance))
            {
                match = System.Text.RegularExpressions.Regex.Match(
                    message, @"on instance ""([^""]+)""");
                if (match.Success)
                {
                    instance = match.Groups[1].Value.Trim('"');
                }
            }

            // Match: [{Category}] prefix (like [radarr], [sonarr])
            if (string.IsNullOrEmpty(instance))
            {
                match = System.Text.RegularExpressions.Regex.Match(
                    message, @"^\[(\w+)\]");
                if (match.Success)
                {
                    instance = match.Groups[1].Value;
                }
            }
        }

        // Clean instance name - remove quotes and special chars
        if (!string.IsNullOrEmpty(instance))
        {
            instance = instance.Trim('"', ' ', '\'', '', '】', '【');
        }

        // Check for FreeSpace messages - route to separate log file
        var isFreeSpace = message.Contains("FreeSpace:");
        string fileName;
        
        if (isFreeSpace)
        {
            fileName = $"freespace-{_date:yyyyMMdd}.log";
        }
        else if (string.IsNullOrEmpty(instance))
        {
            fileName = $"torrentarr-{_date:yyyyMMdd}.log";
        }
        else
        {
            fileName = $"worker-{instance.ToLowerInvariant()}-{_date:yyyyMMdd}.log";
        }

        var filePath = Path.Combine(_logsPath, fileName);
        var writer = GetOrCreateWriter(filePath);

        // Format: [yyyy-MM-dd HH:mm:ss][LEVEL   ][Torrentarr.Instance     ]Message
        var timestamp = logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
        var levelAlias = GetLevelAlias(logEvent.Level);
        var loggerName = string.IsNullOrEmpty(instance) ? "Torrentarr" : $"Torrentarr.{instance}";
        var formattedMessage = $"[{timestamp}][{levelAlias}][{loggerName,-20}]{logEvent.RenderMessage()}";

        lock (_lock)
        {
            writer.WriteLine(formattedMessage);
        }
    }

    private static string GetLevelAlias(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose     => "TRACE   ",
        LogEventLevel.Debug       => "DEBUG   ",
        LogEventLevel.Information => "INFO    ",
        LogEventLevel.Warning     => "WARNING ",
        LogEventLevel.Error       => "ERROR   ",
        LogEventLevel.Fatal       => "FATAL   ",
        _                         => "INFO    "
    };

    private StreamWriter GetOrCreateWriter(string path)
    {
        lock (_lock)
        {
            if (_writers.TryGetValue(path, out var existingWriter))
            {
                return existingWriter;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var writer = new StreamWriter(path, append: false) { AutoFlush = true };
            _writers[path] = writer;
            return writer;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var writer in _writers.Values)
            {
                writer.Dispose();
            }
            _writers.Clear();
        }
    }
}
