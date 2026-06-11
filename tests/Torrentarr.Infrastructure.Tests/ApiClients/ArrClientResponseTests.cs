using System.Net;
using FluentAssertions;
using RestSharp;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

public sealed class ArrClientResponseTests
{
    [Fact]
    public void EnsureSuccess_ThrowsArrApiException_OnHttpFailure()
    {
        var response = new RestResponse
        {
            ResponseStatus = ResponseStatus.Completed,
            StatusCode = HttpStatusCode.ServiceUnavailable,
            StatusDescription = "Service Unavailable",
            Content = null
        };

        var act = () => ArrClientResponse.EnsureSuccess(response, "GET /api/v3/movie");

        act.Should().Throw<ArrApiException>()
            .Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public void EnsureSuccess_DoesNotThrow_OnSuccess()
    {
        var response = new RestResponse
        {
            ResponseStatus = ResponseStatus.Completed,
            StatusCode = HttpStatusCode.OK,
            StatusDescription = "OK",
            Content = "[]"
        };

        var act = () => ArrClientResponse.EnsureSuccess(response, "GET /api/v3/movie");

        act.Should().NotThrow();
    }
}
