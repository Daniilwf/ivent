using GameEvent.Infrastructure.Files;

namespace GameEvent.Web.Tests.Files;

/// <summary>Files on disk (D-108): whole or absent, only names made by <see cref="FileNames"/>, nothing outside the folder.</summary>
public sealed class FileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "game-event-tests", "files-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_written_file_reads_back_and_leaves_no_temporary_file()
    {
        var storage = new FileStorage(_root);
        var name = FileNames.Main(Guid.NewGuid(), FileNames.Webp);

        await storage.WriteAsync(name, [1, 2, 3], Ct);

        await using (var stream = storage.OpenRead(name))
        {
            Assert.NotNull(stream);
            Assert.Equal(3, stream.Length);
        }

        Assert.Equal([Path.Combine(storage.Root, name)], Directory.GetFiles(_root));
        storage.Delete(name);
        Assert.Null(storage.OpenRead(name));
    }

    [Fact]
    public async Task A_file_is_never_overwritten()
    {
        var storage = new FileStorage(_root);
        var name = FileNames.Main(Guid.NewGuid(), FileNames.Gif);
        await storage.WriteAsync(name, [1], Ct);

        await Assert.ThrowsAsync<IOException>(() => storage.WriteAsync(name, [2], Ct));

        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("sub/file.webp")]
    [InlineData("")]
    [InlineData("..")]
    public void A_name_with_a_path_is_refused(string name)
    {
        var storage = new FileStorage(_root);

        Assert.Throws<ArgumentException>(() => storage.OpenRead(name));
        Assert.Throws<ArgumentException>(() => storage.Delete(name));
    }

    [Fact]
    public void Names_are_the_id_and_a_known_extension_only()
    {
        var id = Guid.Parse("50000000-0000-0000-0000-00000000000a");

        Assert.Equal("5000000000000000000000000000000a.webp", FileNames.Main(id, FileNames.Webp));
        Assert.Equal($"{id:N}.thumb.gif", FileNames.Thumbnail(id, FileNames.Gif));
        Assert.Throws<ArgumentOutOfRangeException>(() => FileNames.Main(id, "text/html"));
    }
}
