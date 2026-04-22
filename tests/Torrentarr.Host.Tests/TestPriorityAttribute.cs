namespace Torrentarr.Host.Tests;

/// <summary>Lower values run first. Used with <see cref="PriorityOrderer"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestPriorityAttribute : Attribute
{
    public TestPriorityAttribute(int priority) => Priority = priority;

    public int Priority { get; }
}
