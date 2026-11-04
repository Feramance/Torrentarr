using System.Net;
using Polly;
using Polly.Retry;
using RestSharp;
using Serilog;
using System.Diagnostics;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Http;

/// <summary>
/// qBitrr <c>with_retry</c> parity for HTTP API calls.
/// </summary>
public static class HttpRetryHelper
{
    private static readonly ResiliencePipeline<RestResponse> ArrPipeline = CreatePipeline(5, 0.5, 5.0);
    private static readonly ResiliencePipeline<RestResponse> QBitPipeline = CreatePipeline(3, 0.5, 3.0);
    private static readonly ResiliencePropertyKey<string> RequestDescription = new("Torrentarr.HttpOperation");

    public static async Task<RestResponse> ExecuteArrAsync(
        RestClient client,
        RestRequest request,
        CancellationToken ct = default) =>
        await ExecuteAsync(client, request, ArrPipeline, "Arr", ct);

    public static async Task<RestResponse> ExecuteQBitAsync(
        RestClient client,
        RestRequest request,
        CancellationToken ct = default) =>
        await ExecuteAsync(client, request, QBitPipeline, "qBittorrent", ct);

    public static Task<RestResponse> ExecuteQBitOnceAsync(RestClient client, RestRequest request, CancellationToken ct)
        => ExecuteAsync(client, request, null, "qBittorrent", ct);

    private static async Task<RestResponse> ExecuteAsync(RestClient client, RestRequest request,
        ResiliencePipeline<RestResponse>? pipeline, string service, CancellationToken ct)
    {
        var route = request.Resource.Split('?', '#')[0];
        if (Uri.TryCreate(route, UriKind.Absolute, out var absolute)) route = absolute.AbsolutePath;
        var description = DiagnosticText.Safe($"{service} {request.Method} {client.Options.BaseUrl?.Host}:{client.Options.BaseUrl?.Port} /{route.TrimStart('/')}");
        var context = ResilienceContextPool.Shared.Get(ct);
        context.Properties.Set(RequestDescription, description);
        var attempts = 0;
        var started = Stopwatch.GetTimestamp();
        async ValueTask<RestResponse> Attempt(ResilienceContext current)
        {
            var attempt = ++attempts;
            var at = Stopwatch.GetTimestamp();
            Log.Debug("HTTP attempt {Attempt}: {HttpOperation}", attempt, description);
            try
            {
                var response = await client.ExecuteAsync(request, current.CancellationToken);
                Log.Debug("HTTP attempt {Attempt} completed: {HttpOperation}; status={Status}; transport={Transport}; elapsed={ElapsedMs:F1}ms; error={Error}",
                    attempt, description, (int)response.StatusCode, response.ResponseStatus, Stopwatch.GetElapsedTime(at).TotalMilliseconds,
                    response.ErrorException == null ? "none" : DiagnosticText.Error(response.ErrorException));
                return response;
            }
            catch (Exception ex)
            {
                Log.Debug("HTTP attempt {Attempt} ended: {HttpOperation}; cancelled={Cancelled}; elapsed={ElapsedMs:F1}ms; error={Error}",
                    attempt, description, current.CancellationToken.IsCancellationRequested, Stopwatch.GetElapsedTime(at).TotalMilliseconds, DiagnosticText.Error(ex));
                throw;
            }
        }
        try
        {
            var response = pipeline == null ? await Attempt(context) : await pipeline.ExecuteAsync(Attempt, context);
            Log.Debug("HTTP operation finished: {HttpOperation}; attempts={Attempts}; status={Status}; successful={Successful}; elapsed={ElapsedMs:F1}ms",
                description, attempts, (int)response.StatusCode, response.IsSuccessful, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            Log.Debug("HTTP operation ended: {HttpOperation}; attempts={Attempts}; cancelled={Cancelled}; elapsed={ElapsedMs:F1}ms; error={Error}",
                description, attempts, ct.IsCancellationRequested, Stopwatch.GetElapsedTime(started).TotalMilliseconds, DiagnosticText.Error(ex));
            throw;
        }
        finally { ResilienceContextPool.Shared.Return(context); }
    }

    private static ResiliencePipeline<RestResponse> CreatePipeline(int maxAttempts, double backoffSeconds, double maxBackoffSeconds)
    {
        return new ResiliencePipelineBuilder<RestResponse>()
            .AddRetry(new RetryStrategyOptions<RestResponse>
            {
                MaxRetryAttempts = maxAttempts,
                OnRetry = args =>
                {
                    args.Context.Properties.TryGetValue(RequestDescription, out var description);
                    var reason = args.Outcome.Exception is { } error ? DiagnosticText.Error(error)
                        : $"HTTP {(int)(args.Outcome.Result?.StatusCode ?? 0)}; transport={args.Outcome.Result?.ResponseStatus}";
                    Log.Debug("HTTP retry scheduled: {HttpOperation}; afterAttempt={Attempt}; retry={Retry}/{MaximumRetries}; delay={DelayMs:F1}ms; reason={Reason}",
                        description, args.AttemptNumber + 1, args.AttemptNumber + 1, maxAttempts, args.RetryDelay.TotalMilliseconds, reason);
                    return default;
                },
                DelayGenerator = args =>
                {
                    var delay = Math.Min(
                        maxBackoffSeconds,
                        backoffSeconds * Math.Pow(2, args.AttemptNumber));
                    return ValueTask.FromResult<TimeSpan?>(TimeSpan.FromSeconds(delay));
                },
                ShouldHandle = new PredicateBuilder<RestResponse>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested)
                    .HandleResult(r =>
                        (r.ResponseStatus != ResponseStatus.Completed && (int)r.StatusCode == 0)
                        || r.StatusCode is HttpStatusCode.RequestTimeout
                            or HttpStatusCode.TooManyRequests
                            or HttpStatusCode.BadGateway
                            or HttpStatusCode.ServiceUnavailable
                            or HttpStatusCode.GatewayTimeout
                        || (int)r.StatusCode >= 500)
            })
            .Build();
    }
}
