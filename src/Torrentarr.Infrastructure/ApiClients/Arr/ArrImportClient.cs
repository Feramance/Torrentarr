using Newtonsoft.Json.Linq;
using RestSharp;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

internal interface IArrImportClient : IDisposable
{
    Task<JToken> ReadImportAsync(string resource, CancellationToken ct);
    Task<int> SubmitImportAsync(JArray files, string mode, CancellationToken ct);
}

internal static class ArrImportClient
{
    internal static IArrImportClient Create(ArrInstanceConfig config) => config.Type.ToLowerInvariant() switch
    {
        "radarr" => new RadarrClient(config.URI, config.APIKey, config.SkipTLSVerify),
        "sonarr" => new SonarrClient(config.URI, config.APIKey, config.SkipTLSVerify),
        "lidarr" => new LidarrClient(config.URI, config.APIKey, config.SkipTLSVerify),
        "readarr" => new ReadarrClient(config.URI, config.APIKey, config.SkipTLSVerify),
        _ => throw new InvalidOperationException("Unknown Arr type: " + config.Type)
    };

    internal static async Task<JToken> ReadAsync(RestClient client, RestRequest request, CancellationToken ct)
    {
        var response = await ArrClientResponse.ExecuteAsync(client, request, ct);
        ArrClientResponse.EnsureSuccess(response, "GET import evidence");
        return JToken.Parse(response.Content ?? throw new IOException("Missing import evidence."));
    }

    internal static async Task<int> SubmitAsync(RestClient client, RestRequest request, JArray files, string mode, CancellationToken ct)
    {
        // Lidarr's replaceExistingFiles deletes the whole album; normal upgrades already replace selected track files.
        request.AddStringBody(new JObject { ["name"] = "ManualImport", ["importMode"] = mode, ["replaceExistingFiles"] = false, ["files"] = files }
            .ToString(Newtonsoft.Json.Formatting.None), DataFormat.Json);
        // A durable claim exists before this call. Never retry an uncertain import POST.
        var response = await ArrClientResponse.ExecuteOnceAsync(client, request, ct);
        await ArrActivityTracking.RecordAcceptedAsync(request, response, ct);
        ArrClientResponse.EnsureSuccess(response, "POST ManualImport");
        return JObject.Parse(response.Content ?? "{}")["id"]?.Value<int>() is > 0 and var id
            ? id : throw new IOException("Arr accepted an import without a command receipt.");
    }
}

public partial class RadarrClient : IArrImportClient
{
    async Task<JToken> IArrImportClient.ReadImportAsync(string resource, CancellationToken ct)
    {
        var request = new RestRequest("/api/v3/" + resource, Method.Get);
        AddApiKeyHeader(request);
        return await ArrImportClient.ReadAsync(_client, request, ct);
    }

    async Task<int> IArrImportClient.SubmitImportAsync(JArray files, string mode, CancellationToken ct)
    {
        var request = new RestRequest("/api/v3/command", Method.Post);
        AddApiKeyHeader(request);
        return await ArrImportClient.SubmitAsync(_client, request, files, mode, ct);
    }
}

public partial class SonarrClient : IArrImportClient
{
    async Task<JToken> IArrImportClient.ReadImportAsync(string resource, CancellationToken ct)
    {
        var request = await CreateRequestAsync(resource, Method.Get, ct);
        AddApiKeyHeader(request);
        return await ArrImportClient.ReadAsync(_client, request, ct);
    }

    async Task<int> IArrImportClient.SubmitImportAsync(JArray files, string mode, CancellationToken ct)
    {
        var request = await CreateRequestAsync("command", Method.Post, ct);
        AddApiKeyHeader(request);
        return await ArrImportClient.SubmitAsync(_client, request, files, mode, ct);
    }
}

public partial class LidarrClient : IArrImportClient
{
    async Task<JToken> IArrImportClient.ReadImportAsync(string resource, CancellationToken ct)
    {
        var request = new RestRequest("/api/v1/" + resource, Method.Get);
        AddApiKeyHeader(request);
        return await ArrImportClient.ReadAsync(_client, request, ct);
    }

    async Task<int> IArrImportClient.SubmitImportAsync(JArray files, string mode, CancellationToken ct)
    {
        var request = new RestRequest("/api/v1/command", Method.Post);
        AddApiKeyHeader(request);
        return await ArrImportClient.SubmitAsync(_client, request, files, mode, ct);
    }
}

public partial class ReadarrClient : IArrImportClient
{
    async Task<JToken> IArrImportClient.ReadImportAsync(string resource, CancellationToken ct)
    {
        var request = new RestRequest("/api/v1/" + resource, Method.Get);
        AddApiKeyHeader(request);
        return await ArrImportClient.ReadAsync(_client, request, ct);
    }

    async Task<int> IArrImportClient.SubmitImportAsync(JArray files, string mode, CancellationToken ct)
    {
        var request = new RestRequest("/api/v1/command", Method.Post);
        AddApiKeyHeader(request);
        return await ArrImportClient.SubmitAsync(_client, request, files, mode, ct);
    }
}
