using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;
using Torrentarr.Host.Sinks;
using Xunit;

namespace Torrentarr.Host.Tests;

public sealed class WorkerLogEventSinkTests
{
    [Theory]
    [InlineData("FreeSpace: operation failed", "freespace-")]
    [InlineData("UpdateService: GitHub check failed", "torrentarr-")]
    public void Exceptions_AreWrittenWithoutChangingRoutingOrPrefix(string message, string prefix)
    {
        var directory = Directory.CreateTempSubdirectory("torrentarr-logs-");
        try
        {
            var sink = new WorkerLogEventSink(directory.FullName);
            var timestamp = DateTimeOffset.Now;
            var exception = new IOException("pause rejected", new InvalidOperationException("underlying failure"));
            sink.Emit(new LogEvent(timestamp, LogEventLevel.Warning, exception,
                new MessageTemplateParser().Parse(message), []));
            sink.Dispose();
            var path = Directory.GetFiles(directory.FullName).Should().ContainSingle().Subject;
            Path.GetFileName(path).Should().StartWith(prefix);
            var lines = File.ReadAllLines(path);
            var loggerName = "Torrentarr";
            lines[0].Should().Be($"[{timestamp:yyyy-MM-dd HH:mm:ss}][WARNING ][{loggerName,-20}]{message}");
            string.Join('\n', lines.Skip(1)).Should().Contain("System.IO.IOException: pause rejected")
                .And.Contain("System.InvalidOperationException: underlying failure");
        }
        finally { directory.Delete(true); }
    }
}
