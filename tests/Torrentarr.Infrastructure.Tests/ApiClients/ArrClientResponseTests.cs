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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeserializeOrDefault_ReturnsDefaultForMissingContent(string? content)
    {
        var result = ArrClientResponse.DeserializeOrDefault<SystemInfo>(content);

        result.Should().NotBeNull();
        result.Version.Should().BeEmpty();
    }

    [Fact]
    public void DeserializeOrDefault_DeserializesContent()
    {
        var result = ArrClientResponse.DeserializeOrDefault<SystemInfo>("{\"version\":\"4.0.0\"}");

        result.Version.Should().Be("4.0.0");
    }

    [Fact]
    public void IsArrHttpError_TrueFor415()
    {
        var ex = new ArrApiException("failed", HttpStatusCode.UnsupportedMediaType, "");
        ArrClientResponse.IsArrHttpError(ex).Should().BeTrue();
        ArrClientResponse.IsArrTransportError(ex).Should().BeFalse();
    }

    [Fact]
    public void IsArrTransportError_TrueForHttpRequestException()
    {
        var ex = new HttpRequestException("connection refused");
        ArrClientResponse.IsArrTransportError(ex).Should().BeTrue();
        ArrClientResponse.IsArrHttpError(ex).Should().BeFalse();
    }

    [Fact]
    public void IsArrTransportError_TrueForTaskCanceledTimeout()
    {
        var ex = new TaskCanceledException("request timed out");
        ArrClientResponse.IsArrTransportError(ex).Should().BeTrue();
        ArrClientResponse.IsArrHttpError(ex).Should().BeFalse();
    }

    [Fact]
    public void IsArrTransportError_TrueWhenStatusMissing()
    {
        var ex = new ArrApiException("timed out", statusCode: null, "Error");
        ArrClientResponse.IsArrTransportError(ex).Should().BeTrue();
        ArrClientResponse.IsArrHttpError(ex).Should().BeFalse();
    }

    [Fact]
    public void RaiseIfAllEpisodeFetchesFailed_ThrowsWhenEverySeriesFailed()
    {
        var last = new ArrApiException("415", HttpStatusCode.UnsupportedMediaType, "");
        var act = () => ArrClientResponse.RaiseIfAllEpisodeFetchesFailed(3, 3, last);
        act.Should().Throw<ArrApiException>().Which.Should().BeSameAs(last);
    }

    [Fact]
    public void RaiseIfAllEpisodeFetchesFailed_NoOpWhenSomeSucceeded()
    {
        var act = () => ArrClientResponse.RaiseIfAllEpisodeFetchesFailed(3, 1, new Exception("x"));
        act.Should().NotThrow();
    }
}
