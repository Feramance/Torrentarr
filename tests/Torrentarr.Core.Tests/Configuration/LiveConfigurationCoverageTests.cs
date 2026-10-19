using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public class LiveConfigurationCoverageTests
{
    [Fact]
    public void EveryPersistedConfigurationPropertyHasAnApplicationOwner()
    {
        var assembly = typeof(TorrentarrConfig).Assembly;
        using var stream = assembly.GetManifestResourceStream("Torrentarr.Core.Configuration.LiveConfigurationCoverage.json")!;
        var coverage = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        var pending = new Queue<Type>();
        var visited = new HashSet<Type>();
        var fields = new HashSet<string>();
        pending.Enqueue(typeof(TorrentarrConfig));
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(type)) continue;
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanWrite || property.GetCustomAttributes().Any(a => a.GetType().Name == "TomlIgnoreAttribute")) continue;
                fields.Add(property.DeclaringType!.Name + "." + property.Name);
                var child = property.PropertyType;
                if (child.IsGenericType) child = child.GetGenericArguments()[^1];
                if (child.Assembly == assembly && child.Namespace == typeof(TorrentarrConfig).Namespace) pending.Enqueue(child);
            }
        }
        coverage.Keys.Should().BeEquivalentTo(fields);
        coverage.Values.Should().OnlyContain(owner => !string.IsNullOrWhiteSpace(owner));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1T", 1099511627776L)]
    [InlineData(" 1024g ", 1099511627776L)]
    [InlineData("-1", -1)]
    public void ThresholdUsesCheckedBinaryUnits(string input, long expected) => FreeSpaceThreshold.Parse(input).Should().Be(expected);
}
