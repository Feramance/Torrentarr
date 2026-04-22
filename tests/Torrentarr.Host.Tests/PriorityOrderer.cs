using Xunit.Abstractions;
using Xunit.Sdk;

namespace Torrentarr.Host.Tests;

/// <summary>Orders test cases by <see cref="TestPriorityAttribute"/>, then by method name for stability.</summary>
public sealed class PriorityOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase =>
        testCases
            .OrderBy(GetPriority)
            .ThenBy(c => c.TestMethod.Method.Name, StringComparer.Ordinal);

    private static int GetPriority<TTestCase>(TTestCase testCase)
        where TTestCase : ITestCase
    {
        var attr = testCase.TestMethod.Method
            .GetCustomAttributes(typeof(TestPriorityAttribute).AssemblyQualifiedName!)
            .FirstOrDefault();
        return attr?.GetNamedArgument<int>("Priority") ?? 0;
    }
}
