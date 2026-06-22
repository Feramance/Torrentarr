using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class WebUIAuthHelpersTests
{
    [Fact]
    public void TokenEquals_SameToken_ReturnsTrue()
    {
        WebUIAuthHelpers.TokenEquals("secret", "secret").Should().BeTrue();
    }

    [Fact]
    public void TokenEquals_DifferentTokens_ReturnsFalse()
    {
        WebUIAuthHelpers.TokenEquals("secret", "other").Should().BeFalse();
    }

    [Fact]
    public void TokenEquals_NullAndEmpty_ReturnsTrue()
    {
        WebUIAuthHelpers.TokenEquals(null, "").Should().BeTrue();
    }

    [Fact]
    public void TokenEquals_NullAndNonNull_ReturnsFalse()
    {
        WebUIAuthHelpers.TokenEquals(null, "x").Should().BeFalse();
    }

    [Theory]
    [InlineData("", "GET", true)]
    [InlineData("/health", "GET", true)]
    [InlineData("/login", "GET", true)]
    [InlineData("/web/meta", "GET", true)]
    [InlineData("/web/login", "POST", true)]
    [InlineData("/web/logout", "GET", true)]
    [InlineData("/web/logout", "POST", true)]
    [InlineData("/web/auth/set-password", "POST", true)]
    [InlineData("/web/auth/oidc/challenge", "GET", true)]
    [InlineData("/web/auth/oidc/challenge", "POST", false)]
    [InlineData("/signin-oidc", "GET", true)]
    [InlineData("/signin-oidc", "POST", false)]
    [InlineData("/ui", "GET", false)]
    [InlineData("/web/token", "GET", false)]
    [InlineData("/web/config", "GET", false)]
    public void IsPublicPath_ReturnsExpected(string path, string method, bool expected)
    {
        WebUIAuthHelpers.IsPublicPath(path, method).Should().Be(expected);
    }

    [Fact]
    public void IsSetPasswordAllowed_AllowsAuthenticatedCaller()
    {
        var cfg = new TorrentarrConfig { WebUI = new WebUIConfig { Token = "secret" } };
        WebUIAuthHelpers.IsSetPasswordAllowed(cfg, null, isAuthenticated: true, bearerOrQueryToken: null)
            .Should().BeTrue();
    }

    [Fact]
    public void IsSetPasswordAllowed_BearerApiToken_WhenPasswordUnset_AllowsBootstrap()
    {
        var cfg = new TorrentarrConfig { WebUI = { Token = "api-token", PasswordHash = "" } };
        WebUIAuthHelpers.IsSetPasswordAllowed(cfg, setupToken: null, isAuthenticated: false, bearerOrQueryToken: "api-token")
            .Should().BeTrue();
    }

    [Fact]
    public void IsSetPasswordAllowed_BearerApiToken_WhenPasswordAlreadySet_DeniesReset()
    {
        var cfg = new TorrentarrConfig { WebUI = { Token = "api-token", PasswordHash = "hashed" } };
        WebUIAuthHelpers.IsSetPasswordAllowed(cfg, setupToken: null, isAuthenticated: false, bearerOrQueryToken: "api-token")
            .Should().BeFalse();
    }

    [Fact]
    public void IsSetPasswordAllowed_AllowsEnvSetupToken()
    {
        var cfg = new TorrentarrConfig { WebUI = new WebUIConfig { Token = "" } };
        Environment.SetEnvironmentVariable("TORRENTARR_SETUP_TOKEN", "env-setup");
        try
        {
            WebUIAuthHelpers.IsSetPasswordAllowed(cfg, "env-setup", isAuthenticated: false, bearerOrQueryToken: null)
                .Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TORRENTARR_SETUP_TOKEN", null);
        }
    }

    [Fact]
    public void IsSetPasswordAllowed_AllowsQbitrrEnvSetupToken()
    {
        var cfg = new TorrentarrConfig { WebUI = new WebUIConfig { Token = "" } };
        Environment.SetEnvironmentVariable("QBITRR_SETUP_TOKEN", "legacy-setup");
        try
        {
            WebUIAuthHelpers.IsSetPasswordAllowed(cfg, "legacy-setup", isAuthenticated: false, bearerOrQueryToken: null)
                .Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("QBITRR_SETUP_TOKEN", null);
        }
    }

    [Fact]
    public void IsSetPasswordAllowed_AllowsWebUiTokenAsSetupToken_WhenPasswordUnset()
    {
        var cfg = new TorrentarrConfig { WebUI = new WebUIConfig { Token = "config-token", PasswordHash = "" } };
        WebUIAuthHelpers.IsSetPasswordAllowed(cfg, "config-token", isAuthenticated: false, bearerOrQueryToken: null)
            .Should().BeTrue();
    }

    [Fact]
    public void IsSetPasswordAllowed_WebUiTokenAsSetupToken_WhenPasswordAlreadySet_DeniesReset()
    {
        var cfg = new TorrentarrConfig { WebUI = { Token = "config-token", PasswordHash = "hashed" } };
        WebUIAuthHelpers.IsSetPasswordAllowed(cfg, "config-token", isAuthenticated: false, bearerOrQueryToken: null)
            .Should().BeFalse();
    }

    [Fact]
    public void IsSetPasswordAllowed_RejectsMissingSetupToken()
    {
        var cfg = new TorrentarrConfig { WebUI = new WebUIConfig { Token = "secret" } };
        WebUIAuthHelpers.IsSetPasswordAllowed(cfg, null, isAuthenticated: false, bearerOrQueryToken: null)
            .Should().BeFalse();
    }

    [Fact]
    public void IsSetPasswordAllowed_RejectsWrongSetupToken()
    {
        var cfg = new TorrentarrConfig { WebUI = new WebUIConfig { Token = "secret" } };
        Environment.SetEnvironmentVariable("TORRENTARR_SETUP_TOKEN", "expected");
        try
        {
            WebUIAuthHelpers.IsSetPasswordAllowed(cfg, "wrong", isAuthenticated: false, bearerOrQueryToken: null)
                .Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TORRENTARR_SETUP_TOKEN", null);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("new-bcrypt-hash")]
    public void RejectPasswordHashConfigChange_RejectsDirectWrites(string newValue)
    {
        WebUIAuthHelpers.RejectPasswordHashConfigChange("WebUI.PasswordHash", newValue)
            .Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void RejectPasswordHashConfigChange_AllowsRedactedPlaceholder()
    {
        WebUIAuthHelpers.RejectPasswordHashConfigChange("WebUI.PasswordHash", WebUIAuthHelpers.RedactedPlaceholder)
            .Should().BeNull();
    }

    [Fact]
    public void RejectPasswordHashConfigChange_IgnoresOtherKeys()
    {
        WebUIAuthHelpers.RejectPasswordHashConfigChange("WebUI.Port", "8080")
            .Should().BeNull();
    }

    [Fact]
    public void ValidatePasswordHashForConfigApiSave_RejectsChangedHash()
    {
        var current = new TorrentarrConfig { WebUI = { PasswordHash = "existing-hash" } };
        var updated = new TorrentarrConfig { WebUI = { PasswordHash = "" } };

        WebUIAuthHelpers.ValidatePasswordHashForConfigApiSave(current, updated)
            .Should().NotBeNullOrEmpty();
        updated.WebUI.PasswordHash.Should().Be("");
    }

    [Fact]
    public void ValidatePasswordHashForConfigApiSave_RestoresRedactedPlaceholder()
    {
        var current = new TorrentarrConfig { WebUI = { PasswordHash = "existing-hash" } };
        var updated = new TorrentarrConfig { WebUI = { PasswordHash = WebUIAuthHelpers.RedactedPlaceholder } };

        WebUIAuthHelpers.ValidatePasswordHashForConfigApiSave(current, updated).Should().BeNull();
        updated.WebUI.PasswordHash.Should().Be("existing-hash");
    }

    [Fact]
    public void ValidatePasswordHashForConfigApiSave_AllowsUnchangedHash()
    {
        var current = new TorrentarrConfig { WebUI = { PasswordHash = "existing-hash" } };
        var updated = new TorrentarrConfig { WebUI = { PasswordHash = "existing-hash" } };

        WebUIAuthHelpers.ValidatePasswordHashForConfigApiSave(current, updated).Should().BeNull();
    }
}
