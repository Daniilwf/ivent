namespace GameEvent.Engine.Tests.Support;

/// <summary>Files of the repository the tests check against (docs/…), found from the test output folder.</summary>
public static class RepositoryPaths
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GameEvent.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("GameEvent.slnx not found above the test output.");
    }

    public static string Docs(string fileName) => Path.Combine(Root(), "docs", fileName);
}
