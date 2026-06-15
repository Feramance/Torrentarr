using System.Net;
using Polly;
using Polly.Retry;
using RestSharp;

namespace Torrentarr.Infrastructure.Http;

/// <summary>
/// qBitrr <c>with_retry</c> parity for HTTP API calls.
/// </summary>
public static class HttpRetryHelper
{
    private static readonly ResiliencePipeline<RestResponse> ArrPipeline = CreatePipeline(5, 0.5, 5.0);
    private static readonly ResiliencePipeline<RestResponse> QBitPipeline = CreatePipeline(3, 0.5, 3.0);

    public static async Task<RestResponse> ExecuteArrAsync(
        RestClient client,
        RestRequest request,
        CancellationToken ct = default) =>
        await ArrPipeline.ExecuteAsync(async token => await client.ExecuteAsync(request, token), ct);

    public static async Task<RestResponse> ExecuteQBitAsync(
        RestClient client,
        RestRequest request,
        CancellationToken ct = default) =>
        await QBitPipeline.ExecuteAsync(async token => await client.ExecuteAsync(request, token), ct);

    private static ResiliencePipeline<RestResponse> CreatePipeline(int maxAttempts, double backoffSeconds, double maxBackoffSeconds)
    {
        return new ResiliencePipelineBuilder<RestResponse>()
            .AddRetry(new RetryStrategyOptions<RestResponse>
            {
                MaxRetryAttempts = maxAttempts,
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
                        r.ResponseStatus != ResponseStatus.Completed
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
