using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;
using Torrentarr.Infrastructure.Logging;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class LogRetentionTests
{
    [Theory]
    [InlineData("all-20261104.log", true, 3)]
    [InlineData("all-20261104_001.log", true, 3)]
    [InlineData("all-20261104_001.log", false, 1)]
    public void Search_IncludesNumberedSizeRotationsWithoutOtherStreams(string selected, bool includeRotated, int expected)
    {
        var directory = Directory.CreateTempSubdirectory("log-search-rotation-");
        try
        {
            foreach (var name in new[] { "all-20261104.log", "all-20261104_001.log", "all-20261104.log.1", "all-20261103.log", "worker-one-20261104.log" })
                File.WriteAllText(Path.Combine(directory.FullName, name), "marker\n");
            var result = Torrentarr.Infrastructure.Endpoints.LogFileApi.Search(directory.FullName, selected,
                "marker", null, null, includeRotated ? "true" : "false", 10, 0);
            var value = ((Microsoft.AspNetCore.Http.IValueHttpResult)result).Value;
            var json = System.Text.Json.JsonSerializer.SerializeToElement(value);
            json.GetProperty("matches").GetArrayLength().Should().Be(expected);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void SizeRotation_BoundsHistoryAndKeepsAggregateAndServiceRecordsConsistent()
    {
        var directory = Directory.CreateTempSubdirectory("log-retention-");
        try
        {
            using (var sink = new ServiceLogSink(directory.FullName, "Host", 256))
                for (var index = 0; index < 40; index++)
                    sink.Emit(new LogEvent(DateTimeOffset.Now, LogEventLevel.Information, null,
                        new MessageTemplateParser().Parse($"event-{index:D2}-{new string('x', 120)}"), []));
            var aggregate = Directory.GetFiles(directory.FullName, "all-*.log");
            var service = Directory.GetFiles(directory.FullName, "torrentarr-*.log");
            aggregate.Should().HaveCount(8);
            service.Should().HaveCount(8);
            aggregate.Concat(service).Should().OnlyContain(path => new FileInfo(path).Length <= 512,
                "one complete event may cross the size rotation threshold");
            aggregate.SelectMany(File.ReadAllLines).Should().BeEquivalentTo(service.SelectMany(File.ReadAllLines));
        }
        finally { directory.Delete(recursive: true); }
    }
}
