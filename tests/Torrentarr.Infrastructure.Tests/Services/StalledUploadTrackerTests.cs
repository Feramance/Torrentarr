using FluentAssertions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class StalledUploadTrackerTests
{
    private const string Instance = "qBit";

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

        tracker.Touch(Instance, "abc", isStalledUpload: true).Should().Be(DateTimeOffset.UnixEpoch);
        tracker.IdleMeets(Instance, "abc", 10).Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(10));
        tracker.IdleMeets(Instance, "abc", 10).Should().BeTrue();
    }

    [Fact]
    public void Touch_NotStalled_ClearsClock()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new StalledUploadTracker(time);
        tracker.Touch(Instance, "abc", true);
        time.Advance(TimeSpan.FromSeconds(10));
        tracker.Touch(Instance, "abc", false).Should().BeNull();
        tracker.IdleMeets(Instance, "abc", 1).Should().BeFalse();
    }

    [Fact]
    public void Evict_RemovesStamp()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new StalledUploadTracker(time);
        tracker.Touch(Instance, "abc", true);
        time.Advance(TimeSpan.FromSeconds(30));
        tracker.Evict(Instance, "abc");
        tracker.IdleMeets(Instance, "abc", 1).Should().BeFalse();
    }

    [Fact]
    public void SameHash_DifferentInstances_IndependentClocks()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new StalledUploadTracker(time);

        tracker.Touch("qBit", "abc", true);
        time.Advance(TimeSpan.FromSeconds(60));
        tracker.Touch("qBit-seedbox", "abc", true);
        tracker.IdleMeets("qBit", "abc", 60).Should().BeTrue();
        tracker.IdleMeets("qBit-seedbox", "abc", 60).Should().BeFalse();

        tracker.Touch("qBit", "abc", false);
        tracker.IdleMeets("qBit", "abc", 1).Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(60));
        tracker.IdleMeets("qBit-seedbox", "abc", 60).Should().BeTrue();
        tracker.IdleMeets("qBit", "abc", 60).Should().BeFalse();
    }
}
