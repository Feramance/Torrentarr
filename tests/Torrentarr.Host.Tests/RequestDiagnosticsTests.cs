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
    public async Task RequestValuesCannotForgeLogLinesOrTerminalEscapes()
    {
        var sink = new Sink();
        using var log = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET\r\n[ERROR] forged\u001b[31m";
        context.TraceIdentifier = "request\r\nforged\u0085\u2028";
        await new RequestDiagnosticsMiddleware(_ => Task.CompletedTask, log).InvokeAsync(context);
        var entry = sink.Events.Should().ContainSingle().Subject;
        foreach (var name in new[] { "Method", "RequestId" })
        {
            var value = ((ScalarValue)entry.Properties[name]).Value!.ToString()!;
            value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029').Should().BeFalse();
        }
        entry.RenderMessage().Should().NotContain("\n").And.NotContain("\r").And.NotContain("\u001b");
    }

    [Fact]
    public async Task ExceptionHasOneSanitizedErrorAndPropagates()
    {
        var sink = new Sink();
        using var log = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var middleware = new RequestDiagnosticsMiddleware(_ => throw new InvalidOperationException("password=secret"), log);
        await FluentActions.Awaiting(() => middleware.InvokeAsync(new DefaultHttpContext())).Should().ThrowAsync<InvalidOperationException>();
        sink.Events.Should().ContainSingle().Which.Level.Should().Be(LogEventLevel.Error);
        sink.Events[0].RenderMessage().Should().Contain("unmatched route").And.Contain("500").And.NotContain("password=secret");
    }

    [Theory]
    [InlineData(false, 503)]
    [InlineData(true, 504)]
    public async Task DependencyFailures_ReturnSafeUnavailableResponse(bool timeout, int status)
    {
        var sink = new Sink();
        using var log = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new RequestDiagnosticsMiddleware(_ => throw (timeout
            ? new OperationCanceledException("password=secret") : new HttpRequestException("secret response")), log);
        await middleware.InvokeAsync(context);
        context.Response.StatusCode.Should().Be(status);
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).Should().NotContain("secret");
        sink.Events.Should().ContainSingle().Which.RenderMessage().Should().Contain(status.ToString()).And.NotContain("secret response");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellation_IsDebugAndNeverAnApplicationFailure(bool disconnected)
    {
        var sink = new Sink();
        using var log = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        await new RequestDiagnosticsMiddleware(_ => throw (disconnected
            ? new IOException("Client disconnected") : new OperationCanceledException(cancellation.Token)), log).InvokeAsync(context);
        context.Response.StatusCode.Should().Be(499);
        sink.Events.Should().ContainSingle().Which.Level.Should().Be(LogEventLevel.Debug);
        sink.Events[0].RenderMessage().Should().Contain("499").And.NotContain("500");
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
