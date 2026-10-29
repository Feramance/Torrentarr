using FluentAssertions;
using Torrentarr.Core.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class SearchInterfaceCompatibilityTests
{
    [Fact]
    public async Task DeadlineOverloads_ForwardToLegacyImplementations()
    {
        using var token = new CancellationTokenSource();
        var executor = new LegacyExecutor();
        ISearchExecutor search = executor;
        (await search.ExecuteSearchesAsync("Arr", [], DateTimeOffset.UtcNow.AddMinutes(2), token.Token)).Should().BeSameAs(executor.Result);
        executor.Token.Should().Be(token.Token);
        var media = new LegacyMedia();
        IArrMediaService service = media;
        (await service.SearchMissingMediaAsync("movies", DateTimeOffset.UtcNow, token.Token)).Should().BeSameAs(media.Result);
        (await service.SearchQualityUpgradesAsync("movies", DateTimeOffset.UtcNow, token.Token)).Should().BeSameAs(media.Result);
        media.Token.Should().Be(token.Token);
    }

    private sealed class LegacyExecutor : ISearchExecutor
    {
        public SearchResult Result { get; } = new();
        public CancellationToken Token { get; private set; }
        public Task<SearchResult> ExecuteSearchesAsync(string name, IEnumerable<SearchCandidate> candidates, CancellationToken cancellationToken = default)
        { Token = cancellationToken; return Task.FromResult(Result); }
        public Task<int> GetActiveCommandCountAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public bool CanSearch(int active, int limit) => active < limit;
    }

    private sealed class LegacyMedia : IArrMediaService
    {
        public SearchResult Result { get; } = new();
        public CancellationToken Token { get; private set; }
        public Task<SearchResult> SearchMissingMediaAsync(string category, CancellationToken cancellationToken = default)
        { Token = cancellationToken; return Task.FromResult(Result); }
        public Task<SearchResult> SearchQualityUpgradesAsync(string category, CancellationToken cancellationToken = default)
        { Token = cancellationToken; return Task.FromResult(Result); }
        public Task<List<WantedMedia>> GetWantedMediaAsync(string category, CancellationToken cancellationToken = default) => Task.FromResult(new List<WantedMedia>());
        public Task<QualityUpgradeResult> GetCustomFormatUnmetMediaAsync(string category, CancellationToken cancellationToken = default) => Task.FromResult(new QualityUpgradeResult());
    }
}
