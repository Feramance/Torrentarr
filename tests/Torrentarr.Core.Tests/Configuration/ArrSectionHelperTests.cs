using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class ArrSectionHelperTests
{
    [Theory]
    [InlineData("Radarr-Movies", "radarr")]
    [InlineData("radarr", "radarr")]
    [InlineData("Sonarr-TV", "sonarr")]
    [InlineData("Lidarr-Music", "lidarr")]
    [InlineData("Readarr-Books", "readarr")]
    [InlineData("readarr-comics", "readarr")]
    [InlineData("qBit", null)]
    [InlineData("Settings", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ArrTypeFromSectionName_MatchesExpected(string? name, string? expected)
    {
        ArrSectionHelper.ArrTypeFromSectionName(name).Should().Be(expected);
        ArrSectionHelper.IsArrSection(name).Should().Be(expected != null);
    }

    [Fact]
    public void SupportsRequestIntegration_OnlyRadarrSonarr()
    {
        ArrSectionHelper.SupportsRequestIntegration("radarr").Should().BeTrue();
        ArrSectionHelper.SupportsRequestIntegration("sonarr").Should().BeTrue();
        ArrSectionHelper.SupportsRequestIntegration("lidarr").Should().BeFalse();
        ArrSectionHelper.SupportsRequestIntegration("readarr").Should().BeFalse();
    }

    [Fact]
    public void SupportsSearchByYear_AllExceptLidarr()
    {
        ArrSectionHelper.SupportsSearchByYear("radarr").Should().BeTrue();
        ArrSectionHelper.SupportsSearchByYear("sonarr").Should().BeTrue();
        ArrSectionHelper.SupportsSearchByYear("readarr").Should().BeTrue();
        ArrSectionHelper.SupportsSearchByYear("lidarr").Should().BeFalse();
    }

    [Fact]
    public void DefaultFileExtensionAllowlist_ReadarrIncludesAudiobooks()
    {
        ArrSectionHelper.DefaultFileExtensionAllowlist("readarr").Should().Contain(".m4b");
        ArrSectionHelper.DefaultFileExtensionAllowlist("readarr").Should().Contain(".epub");
        ArrSectionHelper.DefaultFileExtensionAllowlist("lidarr").Should().Contain(".flac");
        ArrSectionHelper.DefaultFileExtensionAllowlist("lidarr").Should().NotContain(".log");
        ArrSectionHelper.DefaultFileExtensionAllowlist("lidarr").Should().NotContain(".cue");
        ArrSectionHelper.DefaultFileExtensionAllowlist("radarr").Should().Contain(".mkv");
    }

    [Fact]
    public void DefaultFolderExclusionRegex_IsTypeSpecific()
    {
        ArrSectionHelper.DefaultFolderExclusionRegex("radarr").Should().Contain(@"\bnc(ed|op)?(\\d+)?\b");
        ArrSectionHelper.DefaultFolderExclusionRegex("lidarr").Should().NotContain(@"\bnc(ed|op)?(\\d+)?\b");
        ArrSectionHelper.DefaultFolderExclusionRegex("sonarr", "anime")
            .Should().Contain(@"\bova\b");
    }

    [Fact]
    public void DefaultFileNameExclusionRegex_VideoIncludesNcop()
    {
        ArrSectionHelper.DefaultFileNameExclusionRegex("sonarr").Should().Contain(@"\bncop\\d+?\b");
        ArrSectionHelper.DefaultFileNameExclusionRegex("lidarr").Should().NotContain(@"\bncop\\d+?\b");
        ArrSectionHelper.DefaultFileNameExclusionRegex("readarr").Should().Contain(@"\bsample\b");
    }

    [Fact]
    public void IsUnmodifiedReadarrEbookOnlyAllowlist_DetectsOriginalDefault()
    {
        ArrSectionHelper.IsUnmodifiedReadarrEbookOnlyAllowlist(ArrSectionHelper.ReadarrEbookOnlyAllowlist)
            .Should().BeTrue();
        ArrSectionHelper.IsUnmodifiedReadarrEbookOnlyAllowlist(ArrSectionHelper.ReadarrAllowlist)
            .Should().BeFalse();
        ArrSectionHelper.IsUnmodifiedReadarrEbookOnlyAllowlist([".epub", ".custom"])
            .Should().BeFalse();
    }

    [Fact]
    public void IsEbookOrComicExtension_RecognizesKnownSuffixes()
    {
        ArrSectionHelper.IsEbookOrComicExtension(".epub").Should().BeTrue();
        ArrSectionHelper.IsEbookOrComicExtension("pdf").Should().BeTrue();
        ArrSectionHelper.IsEbookOrComicExtension(".mkv").Should().BeFalse();
    }
}
