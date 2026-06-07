using FluentAssertions;
using RestSharp;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

public sealed class ArrApiResponseHelperTests
{
    [Fact]
    public void DeserializeListOrThrow_SuccessfulEmptyArray_ReturnsEmptyList()
    {
        var response = new RestResponse
        {
            IsSuccessStatusCode = true,
            ResponseStatus = ResponseStatus.Completed,
            Content = "[]"
        };

        var result = ArrApiResponseHelper.DeserializeListOrThrow<string>(response, "test GET");

        result.Should().BeEmpty();
    }

    [Fact]
    public void DeserializeListOrThrow_FailedResponse_ThrowsHttpRequestException()
    {
        var response = new RestResponse
        {
            IsSuccessStatusCode = false,
            ResponseStatus = ResponseStatus.Error,
            StatusCode = System.Net.HttpStatusCode.Unauthorized,
            StatusDescription = "Unauthorized"
        };

        var act = () => ArrApiResponseHelper.DeserializeListOrThrow<string>(response, "Radarr GET /api/v3/movie");

        act.Should().Throw<HttpRequestException>()
            .WithMessage("*401*Unauthorized*");
    }

    [Fact]
    public void DeserializeOrThrow_FailedResponse_ThrowsHttpRequestException()
    {
        var response = new RestResponse
        {
            IsSuccessStatusCode = false,
            ResponseStatus = ResponseStatus.Error,
            StatusCode = System.Net.HttpStatusCode.ServiceUnavailable,
            StatusDescription = "Service Unavailable"
        };

        var act = () => ArrApiResponseHelper.DeserializeOrThrow<QueueResponse>(response, "Radarr GET /api/v3/queue");

        act.Should().Throw<HttpRequestException>()
            .WithMessage("*503*Service Unavailable*");
    }
}
