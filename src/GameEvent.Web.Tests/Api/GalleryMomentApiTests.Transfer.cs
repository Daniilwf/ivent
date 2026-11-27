using System.Net;
using System.Security.Cryptography;
using GameEvent.Engine.Gallery;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Reactions;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Undo;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ArchiveTransfer = GameEvent.Infrastructure.Seasons.SeasonTransfer;

namespace GameEvent.Web.Tests.Api;

public sealed partial class GalleryMomentApiTests
{
    [Fact]
    public async Task Integrity_reports_a_missing_binding_for_a_locally_registered_gallery_file()
    {
        await EnableGalleryAsync();
        using var player = await _site.SignedInAsync("vasya");
        using var created = await UploadAsync(player, SeasonId, Guid.NewGuid());
        await ReadItemAsync(created, HttpStatusCode.Created);

        await using var db = _site.NewDb();
        var fileId = await db.Files.AsNoTracking().Select(file => file.Id).SingleAsync(Ct);
        Assert.True((await SeasonIntegrity.CheckAsync(db, SeasonId, Ct))!.IsIntact);
        Assert.Equal(1, await db.GalleryMediaBindings.Where(binding => binding.FileId == fileId).ExecuteDeleteAsync(Ct));

        var damaged = await SeasonIntegrity.CheckAsync(db, SeasonId, Ct);
        Assert.NotNull(damaged);
        Assert.Contains($"galleryMedia {fileId}", damaged.Differences);
    }

    [Fact]
    public async Task A_season_archive_remaps_the_restored_moment_but_cannot_reuse_another_seasons_local_image()
    {
        await EnableGalleryAsync();
        using var localPlayer = await _site.SignedInAsync("vasya");
        using var viewer = await _site.SignedInAsync("zritel");
        var itemId = Guid.NewGuid();
        var screenshot = Png();
        using var created = await UploadAsync(localPlayer, SeasonId, itemId, bytes: screenshot);
        var localItem = await ReadItemAsync(created, HttpStatusCode.Created);
        await using var db = _site.NewDb();
        var file = await db.Files.AsNoTracking().SingleAsync(Ct);

        // Two independent hosts can publish the same item and file ids. Keep the owner login the same as well.
        await using var origin = new SiteFactory();
        await origin.SeedAsync(withSeason: false);
        var archivedSeason = await origin.CreateSeasonAsync();
        var originActor = origin.Users["vasya"];
        var localActor = _site.Users["vasya"];
        Assert.NotEqual(originActor, localActor);
        var archivedPlayer = Guid.NewGuid();
        var originRules = GalleryRules(RulesetJson.Default());
        await SendOriginAsync(new ChangeRuleset(originRules with { Features = originRules.Features with { Reactions = true } }));
        await SendOriginAsync(new AddSeasonPlayer(archivedPlayer, originActor, "vasya"));
        await SendOriginAsync(new ChangeSeasonStatus(SeasonStatus.Active));

        var originStorage = origin.Services.GetRequiredService<FileStorage>();
        foreach (var (name, url) in new[]
        {
            (FileNames.Main(file.Id, file.MediaType), localItem.ImageUrl),
            (FileNames.Thumbnail(file.Id, file.MediaType), localItem.ThumbnailUrl),
        })
        {
            var bytes = await viewer.GetByteArrayAsync(url, Ct);
            await originStorage.WriteAsync(name, bytes, Ct);
        }

        const string ArchivedCaption = "Момент из выгруженного сезона";
        var digest = Convert.ToHexString(SHA256.HashData(screenshot));
        var prepared = new PreparedGalleryFile(file.Id, file.MediaType, file.Bytes, file.Width, file.Height, file.Frames, digest);
        await SendOriginAsync(new PublishGalleryMoment(itemId, originActor, ArchivedCaption, digest), originActor, prepared);
        var moderator = origin.Users["admin"];
        var originalViewer = origin.Users["zritel"];
        var emoji = originRules.Social.Reactions[0];
        await SendOriginAsync(new SetGalleryReaction(itemId, originalViewer, emoji, Selected: true), originalViewer);
        var deselected = await SendOriginAsync(new SetGalleryReaction(itemId, originalViewer, emoji, Selected: false), originalViewer);
        await SendOriginAsync(new UndoCommand(deselected, "Restore selected reaction"), moderator);
        var hide = await SendOriginAsync(new HideGalleryMoment(itemId), moderator);
        await SendOriginAsync(new UndoCommand(hide, "Восстановить момент"), moderator);

        await using var originDb = origin.NewDb();
        var archive = await ArchiveTransfer.ExportAsync(originDb, archivedSeason, origin.Clock.UtcNow, Ct);
        Assert.NotNull(archive);
        using var zip = new MemoryStream();
        await ArchiveTransfer.WriteZipAsync(archive, zip, Ct);
        zip.Position = 0;
        var imported = await ArchiveTransfer.ImportAsync(db, await ArchiveTransfer.ReadZipAsync(zip, Ct), _site.Clock.UtcNow, ct: Ct);
        Assert.True(imported.Integrity.IsIntact, string.Join("; ", imported.Integrity.Differences));
        Assert.Contains(imported.MatchedUsers, user => user.Login == "vasya");
        Assert.Contains(imported.MatchedUsers, user => user.Login == "zritel");

        var stored = await db.GalleryMoments.AsNoTracking().SingleAsync(moment => moment.SeasonId == archivedSeason, Ct);
        Assert.Equal((itemId, localActor, archivedPlayer, file.Id, ArchivedCaption, false),
            (stored.ItemId, stored.UserId, stored.PlayerId, stored.FileId, stored.Caption, stored.Hidden));
        var localBinding = await db.GalleryMediaBindings.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((SeasonId, itemId, file.Id), (localBinding.SeasonId, localBinding.ItemId, localBinding.FileId));

        var visible = Assert.Single(await ItemsAsync(viewer, archivedSeason));
        Assert.Equal((itemId, ArchivedCaption), (visible.Id, visible.Caption));
        using var gallery = await viewer.GetAsync(Gallery(archivedSeason), Ct);
        var item = Assert.Single((await ApiCalls.OkAsync(gallery)).GetProperty("items").EnumerateArray());
        var selected = Assert.Single(item.GetProperty("reactions").EnumerateArray(), reaction => reaction.GetProperty("selected").GetBoolean());
        Assert.Equal((emoji, 1), (selected.GetProperty("emoji").GetString(), selected.GetProperty("count").GetInt32()));
        await AssertMissingAsync(viewer, visible.ImageUrl);
        await AssertMissingAsync(viewer, visible.ThumbnailUrl);
        await AssertImageAsync(viewer, localItem.ImageUrl);
        await AssertImageAsync(viewer, localItem.ThumbnailUrl);

        async Task<Guid> SendOriginAsync(ICommand command, Guid? authorId = null, PreparedGalleryFile? image = null)
        {
            var commandId = Guid.NewGuid();
            var bus = origin.Services.GetRequiredService<CommandBus>();
            var outcome = await bus.SendAsync(new CommandEnvelope(commandId, archivedSeason, command, authorId, image), Ct);
            Assert.True(outcome.IsAccepted, outcome.Rejection?.Detail);
            return commandId;
        }
    }
}
