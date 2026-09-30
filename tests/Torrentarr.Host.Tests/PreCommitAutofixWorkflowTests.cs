using FluentAssertions;
using Xunit;

namespace Torrentarr.Host.Tests;

public class PreCommitAutofixWorkflowTests
{
    [Fact]
    public void Workflow_RunsForBranchesOnly()
    {
        var workflow = GetWorkflowText();

        workflow.Should().Contain("  push:\n    branches:\n    - '**'");
        workflow.Should().Contain("if: github.event.deleted == false && startsWith(github.ref, 'refs/heads/')");
        workflow.Should().NotContain("tags-ignore");
    }

    [Fact]
    public void Workflow_DoesNotRunForDeletedBranchPushes()
    {
        var workflow = GetWorkflowText();

        workflow.Should().Contain("github.event.deleted == false");
    }

    [Fact]
    public void Autofixes_ArePublishedForReviewWithoutWritePermissions()
    {
        var workflow = GetWorkflowText();

        workflow.Should().Contain("permissions:\n  contents: read");
        workflow.Should().Contain("persist-credentials: false");
        workflow.Should().Contain("uses: actions/upload-artifact@");
        workflow.Should().NotContain("contents: write");
        workflow.Should().NotContain("git push");
        workflow.Should().NotContain("commit-autofixes:");
    }

    [Fact]
    public void FormatterHooks_UseExpectedUpdatedRevisions()
    {
        var config = GetRepositoryFileText(".pre-commit-config.yaml");

        config.Should().Contain("rev: v2.16.0");
        config.Should().Contain("rev: v4.0.0-alpha.8");
    }

    private static string GetWorkflowText()
        => GetRepositoryFileText(Path.Combine(".github", "workflows", "pre-commit-autofix.yml"));

    private static string GetRepositoryFileText(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).ReplaceLineEndings("\n");

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath}.");
    }
}
