using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public sealed class ConfigMutationGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "publication-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "config.toml");

    [Fact]
    public void Publication_AllowsProcessingAndNestedSaves_ButNotExclusiveChanges()
    {
        ConfigMutationGuard.EnableProcessCoordination(ConfigPath);
        using var processing = ConfigMutationGuard.EnterProcessing(ConfigPath);
        using (ConfigMutationGuard.EnterPublication(ConfigPath))
        {
            ConfigMutationGuard.OwnsPublication(ConfigPath).Should().BeTrue();
            ConfigMutationGuard.OwnsMutation(ConfigPath).Should().BeFalse();
            using var nested = ConfigMutationGuard.EnterPublication(ConfigPath);
            using var reader = ConfigMutationGuard.EnterProcessing(ConfigPath);
            new ConfigurationLoader(ConfigPath).SaveConfig(new());
            FluentActions.Invoking(() => ConfigMutationGuard.EnterMutation(ConfigPath)).Should().Throw<ConfigurationBusyException>();
        }
        ConfigMutationGuard.OwnsPublication(ConfigPath).Should().BeFalse();
    }

    [Fact]
    public async Task Publication_RejectsAnotherWriterInAnIndependentContext()
    {
        using var publication = ConfigMutationGuard.EnterPublication(ConfigPath);
        Task other;
        using (ExecutionContext.SuppressFlow())
            other = Task.Run(() =>
            {
                FluentActions.Invoking(() => ConfigMutationGuard.EnterPublication(ConfigPath)).Should().Throw<ConfigurationBusyException>();
                FluentActions.Invoking(() => ConfigMutationGuard.EnterMutation(ConfigPath)).Should().Throw<ConfigurationBusyException>();
                using var reader = ConfigMutationGuard.EnterProcessing(ConfigPath);
            });
        await other;
    }

    [Theory]
    [InlineData(".write-lock")]
    [InlineData(".publish-lock")]
    public void ExternalExclusiveLocks_BlockPublication_AndFailedAcquisitionReleasesHandles(string suffix)
    {
        Directory.CreateDirectory(_root);
        using (var external = new FileStream(ConfigPath + suffix, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            FluentActions.Invoking(() => ConfigMutationGuard.EnterPublication(ConfigPath)).Should().Throw<ConfigurationBusyException>();
        using var mutation = ConfigMutationGuard.EnterMutation(ConfigPath);
        new ConfigurationLoader(ConfigPath).SaveConfig(new());
    }

    [Fact]
    public void ExternalProcessingLock_AllowsPublication_ButBlocksRename()
    {
        Directory.CreateDirectory(_root);
        using var external = new FileStream(ConfigPath + ".write-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        using (ConfigMutationGuard.EnterPublication(ConfigPath)) new ConfigurationLoader(ConfigPath).SaveConfig(new());
        FluentActions.Invoking(() => ConfigMutationGuard.EnterMutation(ConfigPath)).Should().Throw<ConfigurationBusyException>();
    }

    [Fact]
    public void RenameRecovery_BlocksPublication()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(ConfigPath + ".rename.json", "{}");
        FluentActions.Invoking(() => ConfigMutationGuard.EnterPublication(ConfigPath)).Should().Throw<ConfigurationBusyException>();
        using var recovery = ConfigMutationGuard.EnterMutation(ConfigPath, recovery: true);
        new ConfigurationLoader(ConfigPath).SaveConfig(new());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
