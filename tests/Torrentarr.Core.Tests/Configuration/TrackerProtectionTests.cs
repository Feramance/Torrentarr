using FluentAssertions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class TrackerProtectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
    private static TorrentInfo Torrent(double progress = 1) => new()
    {
        Hash = "fixture",
        ClientInstanceId = "client",
        Progress = progress,
        AddedOn = Now.AddHours(-96).ToUnixTimeSeconds(),
        CompletionOn = progress >= 1 ? Now.AddDays(-3).ToUnixTimeSeconds() : 0,
        State = "stalledUP",
        SeedingTime = 200000,
        Ratio = 10
    };
    private static ProtectionObservation Observation(double hours) => new() { SeedSeconds = hours * 3600, EligibleSince = Now.AddHours(-2) };

    [Theory]
    [InlineData(47, "protected")]
    [InlineData(48, "protected")]
    [InlineData(53.999, "protected")]
    [InlineData(54, "eligible")]
    public void OnlyEncodes_RequiresMandatoryTimeAndMargin(double hours, string expected)
        => TrackerProtectionEvaluator.Evaluate(Torrent(), new() { Policy = "onlyencodes", AllowClientRatioClearance = true }, Observation(hours), Now, true).State.Should().Be(expected);

    [Theory]
    [InlineData(0.1, "eligible")]
    [InlineData(0.1001, "protected")]
    [InlineData(0.55, "protected")]
    public void OnlyEncodes_PartialsCannotClearOnRatioOrTime(double progress, string expected)
        => TrackerProtectionEvaluator.Evaluate(Torrent(progress), new() { Policy = "onlyencodes", AllowClientRatioClearance = true }, Observation(0), Now, true).State.Should().Be(expected);

    [Theory]
    [InlineData(168, "protected")]
    [InlineData(192, "protected")]
    [InlineData(198, "eligible")]
    public void TorrentLeech_PowerUserHasEightDayTimeRoutePlusMargin(double hours, string expected)
    {
        var result = TrackerProtectionEvaluator.Evaluate(Torrent(), new() { Policy = "torrentleech-power" }, Observation(hours), Now, true);
        result.State.Should().Be(expected); result.Deadline.Should().BeNull();
    }

    [Fact]
    public void TorrentLeech_PartialNeverClearsOnSeedTime()
        => TrackerProtectionEvaluator.Evaluate(Torrent(.5), new() { Policy = "torrentleech-power" }, Observation(1000), Now, true).State.Should().Be("protected");

    [Fact]
    public void GraceBoundaryAndTrackerHealthAreRequired()
    {
        var policy = new TrackerProtectionConfig { Policy = "onlyencodes" };
        var state = Observation(60); state.EligibleSince = Now.AddMinutes(-59);
        TrackerProtectionEvaluator.Evaluate(Torrent(), policy, state, Now, true).State.Should().Be("protected");
        state.EligibleSince = Now.AddMinutes(-60);
        TrackerProtectionEvaluator.Evaluate(Torrent(), policy, state, Now, true).State.Should().Be("eligible");
        TrackerProtectionEvaluator.Evaluate(Torrent(), policy, state, Now, false).State.Should().Be("unknown");
    }

    [Fact]
    public void UnknownPastDeadlineNeverClearsProtection()
    {
        var torrent = Torrent(.55); torrent.AddedOn = Now.AddDays(-10).ToUnixTimeSeconds();
        var decision = TrackerProtectionEvaluator.Evaluate(torrent, new() { Policy = "onlyencodes" }, Observation(0), Now, true);
        decision.State.Should().Be("protected"); decision.DeadlineRisk.Should().Be("unachievable");
    }

    [Fact]
    public void RecoveryBoundarySubtractsOutstandingSeedTimeAccountingAndReserve()
    {
        var torrent = Torrent(); torrent.AddedOn = Now.AddHours(-100).ToUnixTimeSeconds();
        var decision = TrackerProtectionEvaluator.Evaluate(torrent, new() { Policy = "onlyencodes" }, Observation(12), Now, true);
        decision.LatestRecoveryStart.Should().Be(Now.AddHours(25));
        TrackerProtectionEvaluator.Evaluate(torrent, new() { Policy = "onlyencodes" }, Observation(12), Now.AddHours(1), true).DeadlineRisk.Should().Be("warning");
        TrackerProtectionEvaluator.Evaluate(torrent, new() { Policy = "onlyencodes" }, Observation(12), Now.AddHours(25), true).DeadlineRisk.Should().Be("critical");
    }

    [Fact]
    public void ObservationsDoNotCreditRestartGapsAndResetOnCounterRollback()
    {
        var torrent = Torrent(); var state = new ProtectionObservation();
        TrackerProtectionEvaluator.Observe(state, torrent, true, Now);
        torrent.SeedingTime += 300;
        TrackerProtectionEvaluator.Observe(state, torrent, true, Now.AddMinutes(5));
        state.SeedSeconds.Should().Be(300);
        torrent.SeedingTime += 3600;
        TrackerProtectionEvaluator.Observe(state, torrent, true, Now.AddMinutes(65));
        state.SeedSeconds.Should().Be(300);
        torrent.SeedingTime = 1;
        TrackerProtectionEvaluator.Observe(state, torrent, true, Now.AddMinutes(66));
        state.SeedSeconds.Should().Be(0);
    }

    [Fact]
    public void CounterRecheckDoesNotEraseAnExistingPartialObligation()
    {
        var state = new ProtectionObservation { MaximumDownloadPercent = 55 };
        TrackerProtectionEvaluator.Evaluate(Torrent(.05), new() { Policy = "onlyencodes" }, state, Now, true).State.Should().Be("protected");
    }

    [Fact]
    public void IncompleteEtaIsComparedWithLatestCompletionBoundary()
    {
        var torrent = Torrent(.5); torrent.Eta = 80 * 3600;
        var decision = TrackerProtectionEvaluator.Evaluate(torrent, new() { Policy = "onlyencodes" }, Observation(0), Now, true);
        decision.CompletionRisk.Should().Be("late"); decision.DeadlineRisk.Should().Be("unachievable");
        torrent.Eta = 3600;
        TrackerProtectionEvaluator.Evaluate(torrent, new() { Policy = "onlyencodes" }, Observation(0), Now, true).CompletionRisk.Should().Be("feasible");
    }

    [Theory]
    [InlineData("queuedUP")]
    [InlineData("pausedUP")]
    [InlineData("checkingUP")]
    public void NonSeedingObservationsEarnNoCredit(string stateName)
    {
        var torrent = Torrent(); torrent.State = stateName;
        var state = new ProtectionObservation
        {
            LastObserved = Now.AddMinutes(-5),
            HealthySeeding = true,
            CompletionOn = torrent.CompletionOn,
            ClientSeedSeconds = torrent.SeedingTime - 300
        };
        TrackerProtectionEvaluator.Observe(state, torrent, true, Now);
        state.SeedSeconds.Should().Be(0);
    }

    [Fact]
    public void NonFiniteRatiosNeverClearAnObligation()
    {
        var torrent = Torrent(.5); torrent.Ratio = double.PositiveInfinity;
        TrackerProtectionEvaluator.Evaluate(torrent, new() { Policy = "torrentleech-power", AllowClientRatioClearance = true }, Observation(0), Now, true).State.Should().Be("protected");
    }
    [Fact]
    public void ReaddedTorrentCannotInheritObservedSeedCredit()
    {
        var torrent = Torrent();
        var state = Observation(200000);
        state.AddedOn = torrent.AddedOn - 1;
        TrackerProtectionEvaluator.Observe(state, torrent, true, Now);
        state.SeedSeconds.Should().Be(0);
        state.AddedOn.Should().Be(torrent.AddedOn);
    }
}
