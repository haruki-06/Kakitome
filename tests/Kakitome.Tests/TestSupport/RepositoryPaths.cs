namespace Kakitome.Tests.TestSupport;

internal static class RepositoryPaths
{
    /// <summary>Walks up from the test binary to the directory containing Kakitome.slnx.</summary>
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kakitome.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Kakitome.slnx) not found.");
    }
}
