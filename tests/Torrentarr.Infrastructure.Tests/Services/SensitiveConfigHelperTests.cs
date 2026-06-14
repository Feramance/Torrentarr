using FluentAssertions;
using Newtonsoft.Json.Linq;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class SensitiveConfigHelperTests
{
    [Theory]
    [InlineData("WebUI.PasswordHash", true)]
    [InlineData("WebUI.Token", true)]
    [InlineData("Radarr-1080.APIKey", true)]
    [InlineData("Settings.LoopSleepTimer", false)]
    public void IsSensitiveDottedKey_ClassifiesKeys(string key, bool expected)
    {
        SensitiveConfigHelper.IsSensitiveDottedKey(key).Should().Be(expected);
    }

    [Theory]
    [InlineData("[redacted]")]
    [InlineData("")]
    public void ShouldPreserveExistingSensitiveValue_BlocksClearingSetSecret(string incoming)
    {
        var shouldPreserve = SensitiveConfigHelper.ShouldPreserveExistingSensitiveValue(
            "WebUI.PasswordHash",
            JToken.FromObject(incoming),
            "$2a$11$existinghash");

        shouldPreserve.Should().BeTrue();
    }

    [Fact]
    public void ShouldPreserveExistingSensitiveValue_BlocksNullClear()
    {
        SensitiveConfigHelper.ShouldPreserveExistingSensitiveValue(
                "WebUI.PasswordHash",
                JValue.CreateNull(),
                "$2a$11$existinghash")
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldPreserveExistingSensitiveValue_AllowsSettingWhenUnset()
    {
        SensitiveConfigHelper.ShouldPreserveExistingSensitiveValue(
                "WebUI.PasswordHash",
                JToken.FromObject(""),
                "")
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldPreserveExistingSensitiveValue_AllowsRealRotation()
    {
        SensitiveConfigHelper.ShouldPreserveExistingSensitiveValue(
                "WebUI.PasswordHash",
                JToken.FromObject("$2a$11$newhash"),
                "$2a$11$oldhash")
            .Should().BeFalse();
    }

    [Fact]
    public void GetDottedStringValue_ReadsNestedKey()
    {
        var root = new JObject
        {
            ["WebUI"] = new JObject { ["PasswordHash"] = "secret" }
        };

        SensitiveConfigHelper.GetDottedStringValue(root, "WebUI.PasswordHash")
            .Should().Be("secret");
    }
}
