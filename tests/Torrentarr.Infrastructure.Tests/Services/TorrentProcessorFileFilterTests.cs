using FluentAssertions;
using System.Reflection;
using System.Text.RegularExpressions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tests for TorrentProcessor.ShouldExcludeFile (§2.1 File Filtering).
/// The method is private static — invoked via reflection.
/// Logic:
///   1. Check each folder component against FolderExclusionRegex patterns.
///   2. Check the filename against FileNameExclusionRegex patterns.
///   3. If FileExtensionAllowlist is non-empty, exclude files whose extension is absent.
/// Returns true → file should be excluded (set to priority-0 or torrent deleted).
/// </summary>
public class TorrentProcessorFileFilterTests
{
    private static bool CallShouldExcludeFile(
        string filePath,
        TorrentConfig cfg,
        RegexOptions options = RegexOptions.IgnoreCase)
    {
        var method = typeof(TorrentProcessor)
            .GetMethod("ShouldExcludeFile",
                BindingFlags.NonPublic | BindingFlags.Static);
        return (bool)method!.Invoke(null, new object?[] { filePath, cfg, options })!;
    }

    // ── Extension allowlist ───────────────────────────────────────────────────

    [Fact]
    public void ShouldExclude_ExtensionInAllowlist_NotExcluded()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = [".mkv", ".mp4"]
        };

        CallShouldExcludeFile("Movie/movie.mkv", cfg).Should().BeFalse();
    }

    [Fact]
    public void ShouldExclude_ExtensionNotInAllowlist_Excluded()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = [".mkv", ".mp4"]
        };

        CallShouldExcludeFile("Movie/movie.avi", cfg).Should().BeTrue();
    }

    [Fact]
    public void ShouldExclude_EmptyAllowlist_ExtensionNotChecked()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = []
        };

        // Any extension is fine when allowlist is empty
        CallShouldExcludeFile("Movie/movie.avi", cfg).Should().BeFalse();
    }

    [Fact]
    public void ShouldExclude_ExtensionCaseInsensitive_NotExcluded()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = [".mkv"]
        };

        // ".MKV" should match ".mkv" allowlist entry
        CallShouldExcludeFile("Movie/movie.MKV", cfg).Should().BeFalse();
    }

    // ── Folder exclusion regex ────────────────────────────────────────────────

    [Fact]
    public void ShouldExclude_FolderMatchesRegex_Excluded()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [@"\bextras?\b"],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = []
        };

        CallShouldExcludeFile("Movie/extras/behind-the-scenes.mkv", cfg).Should().BeTrue();
    }

    [Fact]
    public void ShouldExclude_FolderDoesNotMatchRegex_NotExcluded()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [@"\bextras?\b"],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = []
        };

        CallShouldExcludeFile("Movie/movie.mkv", cfg).Should().BeFalse();
    }

    [Theory]
    [InlineData("Movie/extras/clip.mkv")]
    [InlineData("Movie/featurettes/featurette.mkv")]
    [InlineData("Movie/samples/sample.mkv")]
    [InlineData("Movie/screens/screenshot.jpg")]
    public void ShouldExclude_DefaultExcludedFolders_AreExcluded(string path)
    {
        // Default TorrentConfig includes the standard exclusion folders
        var cfg = new TorrentConfig();
        cfg.FileExtensionAllowlist.Clear(); // Remove extension filter to isolate folder test

        CallShouldExcludeFile(path, cfg).Should().BeTrue($"'{path}' is in a default excluded folder");
    }

    [Fact]
    public void ShouldExclude_FolderRegex_CaseInsensitive_WithIgnoreCaseFlag()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [@"\bextras?\b"],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = []
        };

        CallShouldExcludeFile("Movie/EXTRAS/clip.mkv", cfg, RegexOptions.IgnoreCase)
            .Should().BeTrue("folder regex is case-insensitive by default");
    }

    [Fact]
    public void ShouldExclude_FolderRegex_CaseSensitive_NoMatch()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [@"\bextras?\b"],  // lowercase pattern
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = []
        };

        // RegexOptions.None = case-sensitive → "EXTRAS" doesn't match "extras"
        CallShouldExcludeFile("Movie/EXTRAS/clip.mkv", cfg, RegexOptions.None)
            .Should().BeFalse("case-sensitive regex doesn't match uppercase folder");
    }

    // ── Filename exclusion regex ──────────────────────────────────────────────

    [Fact]
    public void ShouldExclude_FileNameMatchesRegex_Excluded()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [],
            FileNameExclusionRegex = [@"\bsample\b"],
            FileExtensionAllowlist = []
        };

        CallShouldExcludeFile("Movie/sample.mkv", cfg).Should().BeTrue();
    }

    [Fact]
    public void ShouldExclude_FileNameDoesNotMatchRegex_NotExcluded()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [],
            FileNameExclusionRegex = [@"\bsample\b"],
            FileExtensionAllowlist = []
        };

        CallShouldExcludeFile("Movie/movie.mkv", cfg).Should().BeFalse();
    }

    [Theory]
    [InlineData("Movie/sample.mkv")]             // matches \bsample\b
    [InlineData("Movie/movie.sample.clip.mkv")]  // matches \bsample\b
    [InlineData("Movie/movie.trailer.mkv")]      // matches \btrailer\b
    [InlineData("Movie/movie.brarbg.com.mkv")]   // matches brarbg.com\b (literal 'b' prefix in pattern)
    public void ShouldExclude_DefaultExcludedFileNames_AreExcluded(string path)
    {
        var cfg = new TorrentConfig();
        cfg.FileExtensionAllowlist.Clear();

        CallShouldExcludeFile(path, cfg).Should().BeTrue($"'{path}' matches a default filename exclusion pattern");
    }

    // ── Backslash path normalization ──────────────────────────────────────────

    [Fact]
    public void ShouldExclude_BackslashPath_NormalizedCorrectly()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [@"\bextras?\b"],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = []
        };

        // Windows-style backslash path should be normalized
        CallShouldExcludeFile(@"Movie\extras\clip.mkv", cfg).Should().BeTrue();
    }

    // ── No filters configured ─────────────────────────────────────────────────

    [Fact]
    public void ShouldExclude_NoFiltersConfigured_NeverExcluded()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = []
        };

        CallShouldExcludeFile("Movie/movie.mkv", cfg).Should().BeFalse();
        CallShouldExcludeFile("Movie/extras/clip.avi", cfg).Should().BeFalse();
    }

    // ── Invalid regex handled gracefully ─────────────────────────────────────

    [Fact]
    public void ShouldExclude_InvalidRegexPattern_SkippedGracefully()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = ["[invalid regex("],  // malformed pattern
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = []
        };

        // Should not throw — bad regex is caught and skipped
        var act = () => CallShouldExcludeFile("Movie/extras/clip.mkv", cfg);
        act.Should().NotThrow();
    }

    // ── Allowlist + folder combined ───────────────────────────────────────────

    [Fact]
    public void ShouldExclude_GoodExtensionButBadFolder_Excluded()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [@"\bextras?\b"],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = [".mkv"]
        };

        // Extension is allowed, but folder is excluded → file should be excluded
        CallShouldExcludeFile("Movie/extras/clip.mkv", cfg).Should().BeTrue();
    }

    [Fact]
    public void ShouldExclude_GoodFolderGoodExtension_NotExcluded()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [@"\bextras?\b"],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = [".mkv"]
        };

        CallShouldExcludeFile("Movie/movie.mkv", cfg).Should().BeFalse();
    }

    // ── Flat file (no subdirectory) ───────────────────────────────────────────

    [Fact]
    public void ShouldExclude_FlatFile_NoFolder_FolderRegexDoesNotApply()
    {
        var cfg = new TorrentConfig
        {
            FolderExclusionRegex = [@"\bextras?\b"],
            FileNameExclusionRegex = [],
            FileExtensionAllowlist = []
        };

        // No subdirectory — folder regex cannot match
        CallShouldExcludeFile("movie.mkv", cfg).Should().BeFalse();
    }
}
