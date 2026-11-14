using FluentAssertions;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Torrentarr.Host.Sinks;
using Xunit;

namespace Torrentarr.Host.Tests;

public sealed class WorkerLogEventSinkTests
{
    [Fact]
    public async Task ContextSurvivesConcurrentScopesAndMatchesConsoleAggregateAndProcessFiles()
    {
        var directory = Directory.CreateTempSubdirectory("torrentarr-log-context-");
        try
        {
            using (var sink = new WorkerLogEventSink(directory.FullName))
            using (var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger())
            {
                await Task.WhenAll(Enumerable.Range(1, 12).Select(async i =>
                {
                    using var instance = Serilog.Context.LogContext.PushProperty("ProcessInstance", "Arr-" + i);
                    using var cycle = Serilog.Context.LogContext.PushProperty("CycleId", "cycle-" + i);
                    using var operation = Serilog.Context.LogContext.PushProperty("Operation", "TorrentCycle");
                    using var client = Serilog.Context.LogContext.PushProperty("ClientInstance", "client-" + i);
                    await Task.Yield();
                    log.ForContext("SourceContext", "TorrentProcessor").ForContext("ProcessId", 42).Verbose("decision {Number}", i);
                }));
            }
            var aggregate = File.ReadAllLines(Directory.GetFiles(directory.FullName, "all-*.log").Single());
            aggregate.Should().HaveCount(12);
            for (var i = 1; i <= 12; i++)
            {
                var line = File.ReadAllLines(Directory.GetFiles(directory.FullName, $"worker-Arr-{i}-*.log").Single()).Should().ContainSingle().Subject;
                line.Should().Contain($"CycleId[cycle-{i}]").And.Contain($"ClientInstance[client-{i}]").And.Contain("SourceContext[TorrentProcessor]").And.Contain("ProcessId[42]");
                aggregate.Should().Contain(line);
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void FormatterRedactsSecretsAndPreservesMultilineExceptionCauses()
    {
        var message = new MessageTemplateParser().Parse("Request password=message-secret failed at https://user:credential-secret@arr.test/private-passkey?apikey=query-secret");
        var entry = new LogEvent(DateTimeOffset.Now, LogEventLevel.Error,
            new IOException("cookie=cookie-secret", new InvalidOperationException("token=inner-secret")), message,
            [new("CycleId", new ScalarValue("cycle-1")), new("ClientInstance", new ScalarValue("client\nforged"))]);
        string Format(bool colour)
        {
            using var writer = new StringWriter();
            new Torrentarr.Infrastructure.Logging.ServiceLogFormatter(colour).Format(entry, writer);
            return System.Text.RegularExpressions.Regex.Replace(writer.ToString(), @"\x1b\[[0-9;]*m", "");
        }
        var plain = Format(false);
        plain.Should().Be(Format(true)).And.Contain("System.IO.IOException").And.Contain("System.InvalidOperationException")
            .And.Contain("[redacted]").And.Contain("CycleId[cycle-1]").And.NotContain("\nforged");
        foreach (var secret in new[] { "message-secret", "credential-secret", "private-passkey", "query-secret", "cookie-secret", "inner-secret" })
            plain.Should().NotContain(secret);
        plain.Split('\n').Length.Should().BeGreaterThan(2);
    }

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
            var path = Directory.GetFiles(directory.FullName).Single(f => Path.GetFileName(f).StartsWith(prefix));
            Path.GetFileName(path).Should().StartWith(prefix);
            var lines = File.ReadAllLines(path);
            var loggerName = "Torrentarr";
            lines[0].Should().Be($"[{timestamp:yyyy-MM-dd HH:mm:ss}][WARNING ][{loggerName,-20}]{message}");
            string.Join('\n', lines.Skip(1)).Should().Contain("System.IO.IOException: pause rejected")
                .And.Contain("System.InvalidOperationException: underlying failure");
        }
        finally { directory.Delete(true); }
    }
    [Fact]
    public void RestartAppends_KeepsRecentHistory_AndExpiresOldFiles()
    {
        var directory = Directory.CreateTempSubdirectory("torrentarr-log-restart-");
        try
        {
            var older = Path.Combine(directory.FullName, $"torrentarr-{DateTime.Now.AddDays(-1):yyyyMMdd}.log");
            var expired = Path.Combine(directory.FullName, "torrentarr-20200101.log");
            File.WriteAllText(older, "old history\n");
            File.WriteAllText(expired, "expired history\n");
            for (var i = 0; i < 2; i++)
            {
                using var sink = new WorkerLogEventSink(directory.FullName);
                sink.Emit(new LogEvent(DateTimeOffset.Now, LogEventLevel.Information, null,
                    new MessageTemplateParser().Parse($"entry-{i}"), []));
            }
            var aggregate = Directory.GetFiles(directory.FullName, "all-*.log").Should().ContainSingle().Subject;
            File.ReadAllLines(aggregate).Should().HaveCount(2);
            var service = Directory.GetFiles(directory.FullName, "torrentarr-*.log").Single(p => p != older);
            File.ReadAllText(service).Should().Be(File.ReadAllText(aggregate));
            File.ReadAllText(older).Should().Be("old history\n");
            File.Exists(expired).Should().BeFalse();
            Directory.GetFiles(directory.FullName, "*.old").Should().BeEmpty();
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("Sonarr-1080", "Sonarr-1080")]
    [InlineData("../../escape", null)]
    [InlineData("Sonarr:TV", null)]
    public void InstanceFilenamesAreSafeAndReadable(string instance, string? expected)
    {
        var safe = Torrentarr.Infrastructure.Logging.ServiceLogSink.SafeName(instance);
        if (expected != null) safe.Should().Be(expected);
        safe.Should().NotContain("/").And.NotContain("\\").And.NotContain("..").And.NotContain(":");
        Torrentarr.Infrastructure.Logging.ServiceLogSink.SafeName("Sonarr/TV").Should().NotBe(
            Torrentarr.Infrastructure.Logging.ServiceLogSink.SafeName("Sonarr:TV"));
    }

    [Fact]
    public void ConsoleUsesColour_AndFilesStayPlain()
    {
        var evt = new LogEvent(DateTimeOffset.Now, LogEventLevel.Error, new IOException("failed"),
            new MessageTemplateParser().Parse("operation failed"), []);
        using var console = new StringWriter();
        using var file = new StringWriter();
        new Torrentarr.Infrastructure.Logging.ServiceLogFormatter(true).Format(evt, console);
        new Torrentarr.Infrastructure.Logging.ServiceLogFormatter().Format(evt, file);
        console.ToString().Should().Contain("\u001b[1;31m");
        file.ToString().Should().NotContain("\u001b[").And.Contain("IOException: failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerContext_ReachesConsoleAggregateAndWorkerFile(bool isolated)
    {
        var directory = Directory.CreateTempSubdirectory("torrentarr-context-");
        var previous = Torrentarr.Core.InstanceContext.Current;
        try
        {
            Torrentarr.Core.InstanceContext.Current = isolated ? null : "Sonarr-TV";
            using var output = new StringWriter();
            using (var sink = new Torrentarr.Infrastructure.Logging.ServiceLogSink(directory.FullName, isolated ? "Worker" : "Host"))
            {
                var properties = new List<LogEventProperty>
                {
                    new("ClientInstance", new ScalarValue("seedbox")),
                    new("TorrentCategory", new ScalarValue("tv")),
                    new("TorrentHash", new ScalarValue("abc"))
                };
                if (isolated) properties.Add(new("ProcessInstance", new ScalarValue("Sonarr-TV")));
                var evt = new LogEvent(DateTimeOffset.Now, LogEventLevel.Information, null,
                    new MessageTemplateParser().Parse("Keeping torrent: H&R obligations remain"), properties);
                sink.Emit(evt);
                new Torrentarr.Infrastructure.Logging.ServiceLogFormatter().Format(evt, output);
            }
            var aggregate = File.ReadAllText(Directory.GetFiles(directory.FullName, "all-*.log").Single());
            aggregate.Should().Be(output.ToString()).And.Contain("Torrentarr.Sonarr-TV")
                .And.Contain("ClientInstance[seedbox]").And.Contain("TorrentCategory[tv]").And.Contain("TorrentHash[abc]");
            File.ReadAllText(Directory.GetFiles(directory.FullName, "worker-Sonarr-TV-*.log").Single()).Should().Be(aggregate);
        }
        finally
        {
            Torrentarr.Core.InstanceContext.Current = previous;
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task ConcurrentProcessWriter()
    {
        var childDirectory = Environment.GetEnvironmentVariable("TORRENTARR_LOG_TEST_CHILD");
        if (childDirectory != null)
        {
            using var sink = new Torrentarr.Infrastructure.Logging.ServiceLogSink(childDirectory, "Worker");
            // The file sink's test clock cannot be reset, so simulate rollover only in a child process.
            var clockTest = Environment.GetEnvironmentVariable("TORRENTARR_LOG_TEST_CLOCK") != null;
            var clock = typeof(Serilog.FileLoggerConfigurationExtensions).Assembly.GetType("Serilog.Sinks.File.Clock")!
                .GetMethod("SetTestDateTimeNow")!;
            for (var i = 0; i < (clockTest ? 10 : 40); i++)
            {
                if (clockTest) clock.Invoke(null, [new DateTime(2026, 1, 1, 23, 59, 59).AddSeconds(i == 0 ? 0 : 2).AddDays(Math.Max(0, i - 1))]);
                sink.Emit(new LogEvent(DateTimeOffset.Now, LogEventLevel.Information,
                    !clockTest && i % 10 == 0 ? new IOException($"concurrent-{Environment.ProcessId}-{i}", new InvalidOperationException("inner cause")) : null,
                    new MessageTemplateParser().Parse($"entry-{Environment.ProcessId}-{i}"),
                    [new LogEventProperty("ProcessInstance", new ScalarValue($"test-{Environment.ProcessId}"))]));
            }
            return;
        }
        var directory = Directory.CreateTempSubdirectory("torrentarr-log-processes-");
        try
        {
            var processes = Enumerable.Range(0, 3).Select(index =>
            {
                var info = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add("vstest");
                info.ArgumentList.Add(typeof(WorkerLogEventSinkTests).Assembly.Location);
                info.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~WorkerLogEventSinkTests.ConcurrentProcessWriter");
                info.Environment["TORRENTARR_LOG_TEST_CHILD"] = index == 2 ? Path.Combine(directory.FullName, "rollover") : directory.FullName;
                if (index == 2) info.Environment["TORRENTARR_LOG_TEST_CLOCK"] = "1";
                return System.Diagnostics.Process.Start(info)!;
            }).ToArray();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try
            {
                await Task.WhenAll(processes.Select(async process =>
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync(timeout.Token);
                    process.ExitCode.Should().Be(0, await stdout + await stderr);
                }));
            }
            finally
            {
                foreach (var process in processes)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.Dispose();
                }
            }
            var lines = File.ReadAllLines(Directory.GetFiles(directory.FullName, "all-*.log").Single());
            var records = lines.Where(line => line.Contains("]entry-")).ToArray();
            records.Should().HaveCount(80).And.OnlyHaveUniqueItems();
            records.Should().OnlyContain(line => line.Contains("[Torrentarr.test-"));
            lines.Count(line => line.StartsWith("System.IO.IOException: concurrent-")).Should().Be(8);
            lines.Count(line => line.Contains("InvalidOperationException: inner cause")).Should().Be(8);
            Directory.GetFiles(directory.FullName, "worker-test-*.log").Should().HaveCount(2);
            var serviceRecords = Directory.GetFiles(directory.FullName, "worker-test-*.log")
                .SelectMany(File.ReadAllLines).Where(line => line.Contains("]entry-")).ToArray();
            serviceRecords.Should().BeEquivalentTo(records);
            var rolled = Directory.GetFiles(Path.Combine(directory.FullName, "rollover"), "all-*.log");
            rolled.Should().HaveCount(7, "the seven-day age limit applies before the eight-file maximum");
            rolled.Select(Path.GetFileName).Should().BeEquivalentTo(Enumerable.Range(4, 7).Select(day => $"all-202601{day:00}.log"));
            foreach (var file in rolled) File.ReadAllLines(file).Should().ContainSingle();
        }
        finally { directory.Delete(true); }
    }
    [Theory]
    [InlineData("Worker starting: \"Sonarr-Anime\"", "Sonarr-Anime")]
    [InlineData("Started updating database for Radarr instance \"Radarr-1080\"", "Radarr-1080")]
    [InlineData("Processing torrents on instance \"Lidarr\"", "Lidarr")]
    [InlineData("[autobrr] working", "autobrr")]
    public void LegacyHostRoutingStillIdentifiesTheService(string message, string instance)
    {
        var directory = Directory.CreateTempSubdirectory("torrentarr-routing-");
        try
        {
            using (var sink = new WorkerLogEventSink(directory.FullName))
                sink.Emit(new LogEvent(DateTimeOffset.Now, LogEventLevel.Information, null,
                    new MessageTemplateParser().Parse(message), []));
            Directory.GetFiles(directory.FullName, $"worker-{instance}-*.log").Should().ContainSingle();
            File.ReadAllText(Directory.GetFiles(directory.FullName, "all-*.log").Single()).Should().Contain($"Torrentarr.{instance}");
        }
        finally { directory.Delete(true); }
    }
}
