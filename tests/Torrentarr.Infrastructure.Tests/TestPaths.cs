namespace Torrentarr.Infrastructure.Tests;

internal static class TestPaths
{
    // macOS exposes its temporary directory through /var, a symlink rejected by path-safety checks.
    public static string TempPath => Resolve(new DirectoryInfo(Path.GetTempPath()));

    private static string Resolve(DirectoryInfo directory)
    {
        if (directory.Parent == null) return directory.FullName;
        var physical = new DirectoryInfo(Path.Combine(Resolve(directory.Parent), directory.Name));
        return physical.ResolveLinkTarget(true)?.FullName ?? physical.FullName;
    }
}
