namespace GameEvent.Infrastructure.Files;

/// <summary>
/// Stored files on disk (D-108): one folder, names from <see cref="FileNames"/> only. A file appears whole or not at
/// all — written next to its place and moved in — so a crash never leaves half a picture to serve.
/// </summary>
public sealed class FileStorage(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public async Task WriteAsync(string name, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        Directory.CreateDirectory(Root);
        var target = PathOf(name);
        var temporary = target + ".part-" + Path.GetRandomFileName();
        try
        {
            await File.WriteAllBytesAsync(temporary, content, ct);
            File.Move(temporary, target, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>The file for reading, or null if it is not there.</summary>
    public FileStream? OpenRead(string name)
    {
        var path = PathOf(name);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, useAsync: true) : null;
    }

    public void Delete(string name) => File.Delete(PathOf(name));

    public bool Exists(string name) => File.Exists(PathOf(name));

    private string PathOf(string name)
    {
        // Names come from FileNames; anything with a path in it is a bug, not a file
        if (name.Length == 0 || name != Path.GetFileName(name) || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Not a stored file name: {name}", nameof(name));
        }

        return Path.Combine(Root, name);
    }
}
