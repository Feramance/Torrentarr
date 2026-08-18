using FluentAssertions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class MetaForceRateLimiterTests : IDisposable
{
    public MetaForceRateLimiterTests() => MetaForceRateLimiter.ResetForTests();

    public void Dispose() => MetaForceRateLimiter.ResetForTests();

    [Fact]
    public void TryAcquire_AllowsSixThenRejects()
    {
        for (var i = 0; i < 6; i++)
            MetaForceRateLimiter.TryAcquire("meta-force:test").Should().BeTrue();

        MetaForceRateLimiter.TryAcquire("meta-force:test").Should().BeFalse();
        MetaForceRateLimiter.TryAcquire("meta-force:other").Should().BeTrue();
    }
}
