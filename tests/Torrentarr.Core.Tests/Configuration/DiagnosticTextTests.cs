using FluentAssertions;
using Torrentarr.Core.Services;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class DiagnosticTextTests
{
    [Theory]
    [InlineData("https://user:password@example.test/passkey/announce?token=secret")]
    [InlineData("password=secret; token=other; apiKey=credential")]
    [InlineData("Authorization=Bearer secret")]
    [InlineData("\"apiKey\": \"secret\"")]
    [InlineData("Bearer secret")]
    public void SecretsAreNeverIncluded(string input)
    {
        var text = DiagnosticText.Safe(input);
        text.Should().NotContain("password@example").And.NotContain("/passkey/").And.NotContain("secret").And.NotContain("credential");
    }

    [Fact]
    public void HttpFailureIncludesStatusWithoutRawResponse()
    {
        var text = DiagnosticText.Error(new HttpRequestException("secret response", null, System.Net.HttpStatusCode.Forbidden));
        text.Should().Contain("403").And.NotContain("secret response");
    }
}
