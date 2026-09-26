namespace GameEvent.Infrastructure.Site;

/// <summary>
/// One process writes the site's data (D-127): the command queue has one consumer. The site holds this lock file while it
/// runs; the one-off commands (the first admin, the imports) take it too, so a command refuses while the site runs and the
/// site does not start while a command does. The operating system frees the lock when the process ends, even killed.
/// </summary>
public sealed class SiteLock : IDisposable
{
    private readonly FileStream _file;

    private SiteLock(FileStream file) => _file = file;

    /// <summary>The lock, or null when another process holds it.</summary>
    public static SiteLock? TryAcquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            return new SiteLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _file.Dispose();
}
