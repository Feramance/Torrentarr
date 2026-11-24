using Xunit;
using System.Net;
using FluentAssertions;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.Tests.Services;

public class SharedFileFilterSourceTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.100.100.200", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("203.0.113.2", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2002:7f00:1::", false)]
    [InlineData("fc00::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void AddressMustBePublic(string address, bool expected)
        => SharedFileFilterSource.IsPublicAddress(IPAddress.Parse(address)).Should().Be(expected);

    [Fact]
    public void WholeListMustValidateBeforeActivation()
    {
        Action compile = () => SharedFileFilterSource.Compile("sample\\.exe$\n[", false);
        compile.Should().Throw<FormatException>();
        Action unsupported = () => SharedFileFilterSource.Compile("(?=secret)", false);
        unsupported.Should().Throw<FormatException>();
    }

    [Fact]
    public void CommentsBomAndCaseOptionsAreSupported()
    {
        var patterns = SharedFileFilterSource.Compile("\uFEFF# reviewed\n\n\\.exe$\n", false);
        patterns.Should().ContainSingle();
        patterns[0].IsMatch("sample.EXE").Should().BeTrue();
        SharedFileFilterSource.Compile("\\.exe$", true)[0].IsMatch("sample.EXE").Should().BeFalse();
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/etc/passwd")]
    [InlineData("file:///etc/passwd")]
    [InlineData("folder/../../outside")]
    [InlineData("C:\\secret")]
    public void LocalPathCannotEscapeDataDirectory(string source)
    {
        Action resolve = () => SharedFileFilterSource.ResolveLocalPath(source, Path.GetTempPath());
        resolve.Should().Throw<FormatException>();
    }

    [Fact]
    public async Task LocalFileReadIsBoundedAndRejectsLinks()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "list.txt");
            await File.WriteAllTextAsync(path, "\\.exe$");
            (await SharedFileFilterSource.ReadAsync("list.txt", directory, default)).Should().Be("\\.exe$");
            await File.WriteAllTextAsync(path, new string('x', SharedFileFilterSource.MaxBytes + 1));
            var oversized = () => SharedFileFilterSource.ReadAsync("list.txt", directory, default);
            await oversized.Should().ThrowAsync<FormatException>();
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(directory, "link.txt"), path);
                var linked = () => SharedFileFilterSource.ReadAsync("link.txt", directory, default);
                await linked.Should().ThrowAsync<FormatException>();
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("http://example.com/list")]
    [InlineData("https://example.com:8443/list")]
    [InlineData("https://user:password@example.com/list")]
    [InlineData("https://example.com/list?token=secret")]
    [InlineData("https://example.com/list#fragment")]
    public async Task UnsafeUrlsAreRejectedBeforeFetching(string source)
    {
        var read = () => SharedFileFilterSource.ReadAsync(source, Path.GetTempPath(), default);
        await read.Should().ThrowAsync<FormatException>();
    }

    [Fact]
    public void PatternLimitsAreEnforced()
    {
        Action tooLong = () => SharedFileFilterSource.Compile(new string('a', 257), false);
        tooLong.Should().Throw<FormatException>();
        Action tooMany = () => SharedFileFilterSource.Compile(string.Join("\n", Enumerable.Repeat("x", 129)), false);
        tooMany.Should().Throw<FormatException>();
    }
}
