using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace Torrentarr.Infrastructure.ApiClients.Arr;

/// <summary>
/// GET raw Arr JSON, set <c>qualityProfileId</c>, PUT the full payload so tags,
/// metadata profile, and other unmapped fields are not wiped by a sparse DTO.
/// </summary>
internal static class ArrQualityProfilePut
{
    internal static void ApplyQualityProfileId(JObject resource, int qualityProfileId) =>
        resource["qualityProfileId"] = qualityProfileId;

    internal static async Task<bool> UpdateAsync(
        RestClient client,
        string apiKey,
        string resourcePath,
        int qualityProfileId,
        CancellationToken ct)
    {
        var get = new RestRequest(resourcePath, Method.Get);
        get.AddHeader("X-Api-Key", apiKey);
        var getResponse = await ArrClientResponse.ExecuteAsync(client, get, ct);
        if (!getResponse.IsSuccessful || string.IsNullOrEmpty(getResponse.Content))
            return false;

        JObject resource;
        try
        {
            resource = JObject.Parse(getResponse.Content);
        }
        catch (JsonException)
        {
            return false;
        }

        ApplyQualityProfileId(resource, qualityProfileId);

        var put = new RestRequest(resourcePath, Method.Put);
        put.AddHeader("X-Api-Key", apiKey);
        put.AddStringBody(resource.ToString(Formatting.None), DataFormat.Json);
        var putResponse = await ArrClientResponse.ExecuteAsync(client, put, ct);
        return putResponse.IsSuccessful;
    }
}
