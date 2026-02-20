using FluentAssertions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class ProcessStateManagerTests
{
    private ProcessStateManager CreateManager() => new ProcessStateManager();

    [Fact]
    public void Initialize_CreatesRetrievableState()
    {
        var mgr = CreateManager();
        var state = new ArrProcessState { Name = "Radarr-Movies", Kind = "radarr", Alive = true };

        mgr.Initialize("Radarr-Movies", state);

        mgr.GetState("Radarr-Movies").Should().NotBeNull();
        mgr.GetState("Radarr-Movies")!.Name.Should().Be("Radarr-Movies");
        mgr.GetState("Radarr-Movies")!.Alive.Should().BeTrue();
    }

    [Fact]
    public void GetState_ReturnsNull_ForUnregisteredName()
    {
        var mgr = CreateManager();

        mgr.GetState("NonExistent").Should().BeNull();
    }

    [Fact]
    public void Update_ModifiesOnlyTargetInstance()
    {
        var mgr = CreateManager();
        mgr.Initialize("Radarr-1", new ArrProcessState { Name = "Radarr-1", Alive = false });
        mgr.Initialize("Sonarr-1", new ArrProcessState { Name = "Sonarr-1", Alive = false });

        mgr.Update("Radarr-1", s => s.Alive = true);

        mgr.GetState("Radarr-1")!.Alive.Should().BeTrue();
        mgr.GetState("Sonarr-1")!.Alive.Should().BeFalse();
    }

    [Fact]
    public void GetAll_ReturnsAllRegisteredInstances()
    {
        var mgr = CreateManager();
        mgr.Initialize("Radarr-1", new ArrProcessState { Name = "Radarr-1" });
        mgr.Initialize("Sonarr-1", new ArrProcessState { Name = "Sonarr-1" });
        mgr.Initialize("Lidarr-1", new ArrProcessState { Name = "Lidarr-1" });

        mgr.GetAll().Should().HaveCount(3);
    }

    [Fact]
    public void Update_IsCaseInsensitive()
    {
        var mgr = CreateManager();
        mgr.Initialize("Radarr-Movies", new ArrProcessState { Name = "Radarr-Movies", Alive = false });

        mgr.Update("radarr-movies", s => s.Alive = true);

        mgr.GetState("Radarr-Movies")!.Alive.Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentUpdates_DoNotThrowOrCorrupt()
    {
        var mgr = CreateManager();
        mgr.Initialize("Instance-1", new ArrProcessState { Name = "Instance-1", QueueCount = 0 });

        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            mgr.Update("Instance-1", s => s.QueueCount = i);
        }));

        var act = async () => await Task.WhenAll(tasks);

        await act.Should().NotThrowAsync();

        // State must still be valid (not null / not thrown)
        mgr.GetState("Instance-1").Should().NotBeNull();
    }
}
