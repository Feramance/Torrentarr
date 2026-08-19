using FluentAssertions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class StalledUploadTrackerTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    [Fact]
    public void Touch_Stalled_SetdefaultsAndIdleMeetsAfterLimit()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new StalledUploadTracker(time);

        tracker.Touch("abc", isStalledUpload: true).Should().Be(DateTimeOffset.UnixEpoch);
        tracker.IdleMeets("abc", 10).Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(10));
        tracker.IdleMeets("abc", 10).Should().BeTrue();
    }

    [Fact]
    public void Touch_NotStalled_ClearsClock()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new StalledUploadTracker(time);
        tracker.Touch("abc", true);
        time.Advance(TimeSpan.FromSeconds(10));
        tracker.Touch("abc", false).Should().BeNull();
        tracker.IdleMeets("abc", 1).Should().BeFalse();
    }

    [Fact]
    public void Evict_RemovesStamp()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new StalledUploadTracker(time);
        tracker.Touch("abc", true);
        time.Advance(TimeSpan.FromSeconds(30));
        tracker.Evict("abc");
        tracker.IdleMeets("abc", 1).Should().BeFalse();
    }
}
