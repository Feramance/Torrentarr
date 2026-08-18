using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class MediaValidationServiceTests
{
    private static MediaValidationService CreateService()
        => new(NullLogger<MediaValidationService>.Instance, new TorrentarrConfig());

    [Fact]
    public async Task ValidateFileAsync_MissingFFprobe_TreatsFileAsValid()
    {
        var service = CreateService();
        service.IsFFprobeAvailable.Should().BeFalse();

        var result = await service.ValidateFileAsync("/tmp/does-not-need-to-exist.mkv");

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().Contain("ffprobe not available");
    }

    [Fact]
    public async Task ValidateFileAsync_EbookExtension_SkipsProbeAsValid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "torrentarr-ffprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var epub = Path.Combine(dir, "book.epub");
        await File.WriteAllTextAsync(epub, "ebook");
        try
        {
            var service = CreateService();
            var result = await service.ValidateFileAsync(epub);

            result.IsValid.Should().BeTrue();
            result.IsMediaFile.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateDirectoryAsync_AllowlistSkipsNonMatchingFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "torrentarr-ffprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "notes.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(dir, "movie.mkv"), "x");
        try
        {
            var service = CreateService();
            var result = await service.ValidateDirectoryAsync(dir, extensionAllowlist: new[] { @"\.mkv" });

            result.Results.Should().ContainSingle(r => r.FilePath.EndsWith("movie.mkv"));
            result.ValidFiles.Should().Be(1);
            result.HasValidMedia.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MatchesExtensionAllowlist_RegexAndPlainExtension()
    {
        MediaValidationService.MatchesExtensionAllowlist("film.mkv", new[] { @"\.mkv" }).Should().BeTrue();
        MediaValidationService.MatchesExtensionAllowlist("film.mkv", new[] { ".mkv" }).Should().BeTrue();
        MediaValidationService.MatchesExtensionAllowlist("film.txt", new[] { ".mkv" }).Should().BeFalse();
    }
}
