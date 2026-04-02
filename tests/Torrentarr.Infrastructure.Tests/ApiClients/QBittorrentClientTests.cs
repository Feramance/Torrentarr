using FluentAssertions;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

/// <summary>
/// Unit tests for <see cref="QBittorrentClient"/> that do not require a running qBit instance.
/// </summary>
public class QBittorrentClientTests
{
    [Fact]
    public async Task TopPriorityAsync_EmptyHashList_ReturnsTrueWithoutHttp()
    {
        var client = new QBittorrentClient("http://127.0.0.1", 8080, "", "");

        var ok = await client.TopPriorityAsync([]);

        ok.Should().BeTrue();
    }
}
