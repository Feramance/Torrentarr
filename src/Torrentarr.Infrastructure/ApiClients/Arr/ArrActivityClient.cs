using Newtonsoft.Json.Linq;
using RestSharp;
namespace Torrentarr.Infrastructure.ApiClients.Arr;

internal interface IArrActivityClient : IDisposable
{
    Task<JArray> ReadActivityAsync(string resource, CancellationToken ct);
}

internal static class ArrActivityResponse
{
    internal static async Task<JArray> ReadAsync(RestClient client, RestRequest request, CancellationToken ct)
    {
        var response = await ArrClientResponse.ExecuteAsync(client, request, ct);
        ArrClientResponse.EnsureSuccess(response, "GET activity evidence");
        return JArray.Parse(response.Content ?? throw new IOException("Missing activity evidence"));
    }
}

public partial class RadarrClient
{
    void IDisposable.Dispose() => _client.Dispose();
    async Task<JArray> IArrActivityClient.ReadActivityAsync(string resource, CancellationToken ct)
    {
        var request = new RestRequest("/api/v3/" + resource, Method.Get);
        AddApiKeyHeader(request);
        return await ArrActivityResponse.ReadAsync(_client, request, ct);
    }
}

public partial class SonarrClient
{
    void IDisposable.Dispose() => _client.Dispose();
    async Task<JArray> IArrActivityClient.ReadActivityAsync(string resource, CancellationToken ct)
    {
        var request = await CreateRequestAsync(resource, Method.Get, ct);
        AddApiKeyHeader(request);
        return await ArrActivityResponse.ReadAsync(_client, request, ct);
    }
}

public partial class LidarrClient
{
    void IDisposable.Dispose() => _client.Dispose();
    async Task<JArray> IArrActivityClient.ReadActivityAsync(string resource, CancellationToken ct)
    {
        var request = new RestRequest("/api/v1/" + resource, Method.Get);
        AddApiKeyHeader(request);
        return await ArrActivityResponse.ReadAsync(_client, request, ct);
    }
}

public partial class ReadarrClient
{
    void IDisposable.Dispose() => _client.Dispose();
    async Task<JArray> IArrActivityClient.ReadActivityAsync(string resource, CancellationToken ct)
    {
        var request = new RestRequest("/api/v1/" + resource, Method.Get);
        AddApiKeyHeader(request);
        return await ArrActivityResponse.ReadAsync(_client, request, ct);
    }
}
