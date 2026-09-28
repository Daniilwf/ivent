using System.Text.Json;
using GameEvent.Web.Site;
using GameEvent.Web.Tests.Api;
using Microsoft.Extensions.Configuration;

namespace GameEvent.Web.Tests.Site;

/// <summary>The version the site reports (J4, D-201): the release tag from the image, else the assembly's version.</summary>
public sealed class SiteVersionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(string? version) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(version is null ? [] : [new KeyValuePair<string, string?>("Site:Version", version)])
            .Build();

    [Fact]
    public void The_release_tag_from_the_image_wins()
    {
        Assert.Equal("v1.2.0", SiteVersion.Of(Config("v1.2.0")));
    }

    [Fact]
    public void Without_a_tag_the_assemblys_version_without_build_metadata()
    {
        var version = SiteVersion.Of(Config(null));

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.DoesNotContain('+', version);
    }

    [Fact]
    public async Task Everyone_reads_the_version_with_the_status()
    {
        await using var site = new SiteFactory();
        await site.SeedAsync();
        var anonymous = await site.AnonymousAsync();

        using var status = JsonDocument.Parse(await anonymous.GetStringAsync("/api/status", Ct));

        Assert.False(string.IsNullOrWhiteSpace(status.RootElement.GetProperty("version").GetString()));
        Assert.False(status.RootElement.GetProperty("maintenance").GetBoolean());
    }
}
