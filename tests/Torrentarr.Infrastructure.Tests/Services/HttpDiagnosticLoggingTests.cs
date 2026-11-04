using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using RestSharp;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Torrentarr.Infrastructure.Http;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

[Collection(nameof(ConfigReloaderTests))]
public sealed class HttpDiagnosticLoggingTests
{
    private sealed class Events : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Items { get; } = new();
        public void Emit(LogEvent logEvent) => Items.Enqueue(logEvent);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public ConcurrentDictionary<string, int> Calls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var count = Calls.AddOrUpdate(path, 1, (_, n) => n + 1);
            if (path == "/cancel") throw new OperationCanceledException(ct);
            var status = path == "/once" || path == "/retry" && count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("private-response-body") });
        }
    }

    [Fact]
    public async Task ExhaustedRetries_LogFinalFailureAndKeepTheOriginalAttemptLimit()
    {
        var events = new Events();
        using var log = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(events).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = log;
        try
        {
            using var handler = new Handler();
            using var client = new RestClient(new RestClientOptions("http://example.test") { ConfigureMessageHandler = _ => handler });
            (await HttpRetryHelper.ExecuteQBitAsync(client, new RestRequest("/once"))).IsSuccessful.Should().BeFalse();
            handler.Calls["/once"].Should().Be(4);
            events.Items.Count(e => e.MessageTemplate.Text.StartsWith("HTTP retry scheduled")).Should().Be(3);
            events.Items.Last().RenderMessage().Should().Contain("attempts=4").And.Contain("status=503").And.Contain("successful=False");
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public async Task ConcurrentRequests_LogAttemptsAndRetriesWithoutCrossingContextsOrLeakingSecrets()
    {
        var events = new Events();
        using var log = new LoggerConfiguration().MinimumLevel.Verbose().Enrich.FromLogContext().WriteTo.Sink(events).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = log;
        try
        {
            using var handler = new Handler();
            using var client = new RestClient(new RestClientOptions("http://example.test") { ConfigureMessageHandler = _ => handler });
            async Task Run(string path, string cycle)
            {
                using var instance = LogContext.PushProperty("ProcessInstance", "Arr-" + cycle);
                using var context = LogContext.PushProperty("CycleId", cycle);
                var request = new RestRequest(path + "?apikey=query-secret");
                request.AddHeader("Authorization", "Bearer header-secret");
                (await HttpRetryHelper.ExecuteArrAsync(client, request)).IsSuccessful.Should().BeTrue();
            }
            await Task.WhenAll(Run("/retry", "A"), Run("/success", "B"));
            handler.Calls["/retry"].Should().Be(2);
            handler.Calls["/success"].Should().Be(1);
            var messages = string.Join('\n', events.Items.Select(e => e.RenderMessage()));
            messages.Should().Contain("HTTP retry scheduled").And.Contain("delay=500.0ms").And.Contain("status=503").And.Contain("attempts=2")
                .And.NotContain("query-secret").And.NotContain("header-secret").And.NotContain("private-response-body");
            foreach (var entry in events.Items)
            {
                var cycle = ((ScalarValue)entry.Properties["CycleId"]).Value!.ToString();
                entry.Properties["ProcessInstance"].ToString().Should().Be('"' + "Arr-" + cycle + '"');
                entry.RenderMessage().Should().Contain(cycle == "A" ? "/retry" : "/success");
            }
        }
        finally { Log.Logger = previous; }
    }

    [Fact]
    public async Task SingleAttemptMutationsAndCancellation_NeverAcquireRetriesFromLogging()
    {
        var events = new Events();
        using var log = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(events).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = log;
        try
        {
            using var handler = new Handler();
            using var client = new RestClient(new RestClientOptions("http://example.test") { ConfigureMessageHandler = _ => handler });
            (await HttpRetryHelper.ExecuteQBitOnceAsync(client, new RestRequest("/once", Method.Post), default)).IsSuccessful.Should().BeFalse();
            handler.Calls["/once"].Should().Be(1);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await FluentActions.Awaiting(() => HttpRetryHelper.ExecuteArrAsync(client, new RestRequest("/cancel"), cancelled.Token))
                .Should().ThrowAsync<OperationCanceledException>();
            events.Items.Should().NotContain(e => e.MessageTemplate.Text.StartsWith("HTTP retry scheduled"));
            events.Items.Should().Contain(e => e.RenderMessage().Contains("cancelled=True"));
        }
        finally { Log.Logger = previous; }
    }
}
