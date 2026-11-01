using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Torrentarr.Infrastructure.Logging;
using Xunit;

namespace Torrentarr.Host.Tests;

public class RequestDiagnosticsTests
{
    private sealed class Sink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    [Theory]
    [InlineData(200, LogEventLevel.Debug)]
    [InlineData(302, LogEventLevel.Debug)]
    [InlineData(404, LogEventLevel.Warning)]
    [InlineData(503, LogEventLevel.Error)]
    public async Task RequestHasOneSafeSummary(int status, LogEventLevel level)
    {
        var sink = new Sink();
        using var log = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/web/private-secret";
        context.Request.QueryString = new("?token=query-secret");
        context.Request.Headers.Authorization = "Bearer header-secret";
        context.TraceIdentifier = "request-123";
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/web/{id}"), 0, null, "test"));
        await new RequestDiagnosticsMiddleware(c => { c.Response.StatusCode = status; return Task.CompletedTask; }, log).InvokeAsync(context);
        var entry = sink.Events.Should().ContainSingle().Subject;
        entry.Level.Should().Be(level);
        entry.RenderMessage().Should().Contain("POST").And.Contain("/web/{id}").And.Contain(status.ToString()).And.Contain("request-123")
            .And.NotContain("private-secret").And.NotContain("query-secret").And.NotContain("header-secret");
        entry.Properties.Should().ContainKey("ElapsedMs");
    }

    [Fact]
    public async Task ExceptionHasOneSanitizedErrorAndPropagates()
    {
        var sink = new Sink();
        using var log = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var middleware = new RequestDiagnosticsMiddleware(_ => throw new HttpRequestException("secret response"), log);
        await FluentActions.Awaiting(() => middleware.InvokeAsync(new DefaultHttpContext())).Should().ThrowAsync<HttpRequestException>();
        sink.Events.Should().ContainSingle().Which.Level.Should().Be(LogEventLevel.Error);
        sink.Events[0].RenderMessage().Should().Contain("unmatched route").And.Contain("500").And.NotContain("secret response");
    }

    [Fact]
    public async Task SuccessfulRequestIsQuietAtInformation()
    {
        var sink = new Sink();
        using var log = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();
        await new RequestDiagnosticsMiddleware(_ => Task.CompletedTask, log).InvokeAsync(new DefaultHttpContext());
        sink.Events.Should().BeEmpty();
    }
}
