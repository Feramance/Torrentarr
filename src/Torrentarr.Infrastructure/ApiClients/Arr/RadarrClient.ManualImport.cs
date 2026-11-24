using Newtonsoft.Json.Linq;
using RestSharp;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

public partial class RadarrClient
{
    internal async Task<JArray> GetManualImportsAsync(string downloadId, CancellationToken ct)
    {
        var request = new RestRequest("/api/v3/manualimport", Method.Get);
        AddApiKeyHeader(request);
        request.AddQueryParameter("downloadId", downloadId);
        request.AddQueryParameter("filterExistingFiles", "true");
        var response = await ArrClientResponse.ExecuteAsync(_client, request, ct);
        ArrClientResponse.EnsureSuccess(response, "GET /api/v3/manualimport");
        return JArray.Parse(response.Content ?? "[]");
    }

    internal async Task<CommandResponse?> ImportMatchedFileAsync(JObject file, CancellationToken ct)
    {
        var request = new RestRequest("/api/v3/command", Method.Post);
        AddApiKeyHeader(request);
        var command = new JObject { ["name"] = "ManualImport", ["importMode"] = "Copy", ["files"] = new JArray(file) };
        request.AddStringBody(command.ToString(Newtonsoft.Json.Formatting.None), DataFormat.Json);
        // The caller persists its claim first. Retrying an uncertain POST could import twice.
        var response = await ArrClientResponse.ExecuteOnceAsync(_client, request, ct);
        await ArrActivityTracking.RecordAcceptedAsync(request, response, ct);
        ArrClientResponse.EnsureSuccess(response, "POST ManualImport");
        return string.IsNullOrWhiteSpace(response.Content) ? null
            : Newtonsoft.Json.JsonConvert.DeserializeObject<CommandResponse>(response.Content);
    }
}
