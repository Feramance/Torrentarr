using System.Net;
using FluentAssertions;
using RestSharp;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

public sealed class ArrApiResponseTests
{
    [Fact]
    public void ParseListOrThrow_SuccessfulEmptyArray_ReturnsEmptyList()
    {
        var response = new RestResponse
        {
            ResponseStatus = ResponseStatus.Completed,
            StatusCode = HttpStatusCode.OK,
            Content = "[]"
        };

        var result = ArrApiResponse.ParseListOrThrow<TestItem>(response, "Test op");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseListOrThrow_SuccessfulPayload_DeserializesList()
    {
        var response = new RestResponse
        {
            ResponseStatus = ResponseStatus.Completed,
            StatusCode = HttpStatusCode.OK,
            Content = """[{"id":1},{"id":2}]"""
        };

        var result = ArrApiResponse.ParseListOrThrow<TestItem>(response, "Test op");

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(1);
    }

    [Fact]
    public void ParseListOrThrow_FailedResponse_Throws()
    {
        var response = new RestResponse
        {
            ResponseStatus = ResponseStatus.Error,
            StatusCode = HttpStatusCode.Unauthorized,
            StatusDescription = "Unauthorized",
            ErrorMessage = "Invalid API key"
        };

        var act = () => ArrApiResponse.ParseListOrThrow<TestItem>(response, "Radarr GetMovies");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Radarr GetMovies failed*401*");
    }

    private sealed class TestItem
    {
        public int Id { get; set; }
    }
}
