using FluentAssertions;
using Xunit;

namespace Torrentarr.Host.Tests;

public class PreCommitAutofixWorkflowTests
{
    [Fact]
    public void Workflow_RunsForBranchesOnly()
    {
        var workflow = GetWorkflowText();

        workflow.Should().Contain("  push:\n    branches:\n      - '**'");
        workflow.Should().Contain("startsWith(github.ref, 'refs/heads/')");
        workflow.Should().NotContain("tags-ignore");
    }

    [Fact]
    public void PushStep_UsesBranchRefspecAndFailsAfterRetries()
    {
        var workflow = GetWorkflowText();

        workflow.Should().Contain("git push origin \"HEAD:refs/heads/${GITHUB_REF_NAME}\"");
        workflow.Should().Contain(
            "          if git push origin \"HEAD:refs/heads/${GITHUB_REF_NAME}\"; then\n" +
            "            exit 0\n" +
            "          fi");
        workflow.Should().Contain("        echo \"Push failed after 3 attempts.\" >&2\n        exit 1");
        workflow.Should().NotContain("git push origin \"HEAD:${GITHUB_REF_NAME}\" && break");
    }

    private static string GetWorkflowText()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ".github", "workflows", "pre-commit-autofix.yml");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).ReplaceLineEndings("\n");

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate .github/workflows/pre-commit-autofix.yml.");
    }
}
