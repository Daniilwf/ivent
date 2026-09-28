using GameEvent.Engine.Accounts;
using GameEvent.Engine.Files;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Queue;

/// <summary>File commands in the queue (D-108): the global log, an active owner, one record per file, the daily limit.</summary>
public sealed class FileQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_file_is_recorded_with_its_event_in_the_global_log()
    {
        await using var h = await QueueHarness.StartAsync();
        var owner = await OwnerAsync(h);
        var fileId = Guid.NewGuid();

        var outcome = await SendAsync(h, Record(fileId, owner));

        Assert.True(outcome.IsAccepted);
        Assert.Equal(new FileStored(fileId, owner, FileNames.Webp, 1000, 640, 480, 1, FileKind.Upload), Assert.Single(outcome.Events).Event);
        await using var db = h.NewDb();
        var record = await db.Files.SingleAsync(Ct);
        Assert.Equal((fileId, owner, FileNames.Webp, h.Clock.UtcNow, FileKind.Upload), (record.Id, record.OwnerId, record.MediaType, record.CreatedAt, record.Kind));
        Assert.Equal(Guid.Empty, (await db.Events.SingleAsync(e => e.Type == "file-stored", Ct)).SeasonId);
    }

    [Fact]
    public async Task A_file_command_sent_to_a_season_is_refused()
    {
        await using var h = await QueueHarness.StartAsync();
        var owner = await OwnerAsync(h);

        var outcome = await h.Bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.NewGuid(), Record(Guid.NewGuid(), owner), owner), Ct);

        Assert.Equal(RejectionCodes.SeasonMismatch, outcome.Rejection!.Code);
    }

    [Fact]
    public async Task An_unknown_or_deleted_owner_is_refused()
    {
        await using var h = await QueueHarness.StartAsync();
        var owner = await OwnerAsync(h);
        Assert.True((await SendAsync(h, new DeleteAccount(owner))).IsAccepted);

        Assert.Equal(FileRules.OwnerUnknown, (await SendAsync(h, Record(Guid.NewGuid(), Guid.NewGuid()))).Rejection!.Code);
        Assert.Equal(FileRules.OwnerUnknown, (await SendAsync(h, Record(Guid.NewGuid(), owner))).Rejection!.Code);
        await using var db = h.NewDb();
        Assert.Empty(await db.Files.ToListAsync(Ct));
    }

    [Fact]
    public async Task One_file_is_recorded_once()
    {
        await using var h = await QueueHarness.StartAsync();
        var owner = await OwnerAsync(h);
        var fileId = Guid.NewGuid();
        Assert.True((await SendAsync(h, Record(fileId, owner))).IsAccepted);

        Assert.Equal(FileRules.AlreadyStored, (await SendAsync(h, Record(fileId, owner))).Rejection!.Code);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    public async Task Only_webp_and_gif_are_recorded(string mediaType)
    {
        await using var h = await QueueHarness.StartAsync();
        var owner = await OwnerAsync(h);

        var outcome = await SendAsync(h, Record(Guid.NewGuid(), owner) with { MediaType = mediaType });

        Assert.Equal(FileRules.MediaTypeInvalid, outcome.Rejection!.Code);
    }

    [Fact]
    public async Task The_daily_limit_counts_the_last_24_hours_of_one_owner()
    {
        await using var h = await QueueHarness.StartAsync();
        var vasya = await OwnerAsync(h, "vasya");
        var petya = await OwnerAsync(h, "petya");
        Assert.True((await SendAsync(h, Record(Guid.NewGuid(), vasya, limit: 2))).IsAccepted);
        h.Clock.UtcNow += TimeSpan.FromHours(1);
        Assert.True((await SendAsync(h, Record(Guid.NewGuid(), vasya, limit: 2))).IsAccepted);

        Assert.Equal(FileRules.DailyLimit, (await SendAsync(h, Record(Guid.NewGuid(), vasya, limit: 2))).Rejection!.Code);
        Assert.True((await SendAsync(h, Record(Guid.NewGuid(), petya, limit: 2))).IsAccepted);

        // The first upload leaves the window after exactly 24 hours
        h.Clock.UtcNow += TimeSpan.FromHours(23);
        Assert.True((await SendAsync(h, Record(Guid.NewGuid(), vasya, limit: 2))).IsAccepted);
    }

    private static RecordFile Record(Guid fileId, Guid owner, int limit = 60) =>
        new(fileId, owner, FileNames.Webp, 1000, 640, 480, 1, limit);

    private static Task<CommandOutcome> SendAsync(QueueHarness h, ICommand command) =>
        h.Bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, command, AuthorId: null), Ct);

    private static async Task<Guid> OwnerAsync(QueueHarness h, string login = "vasya")
    {
        var seeded = await SendAsync(h, new SeedAccount(login, login, Role.Player, "пароль-игрока-1"));
        return Assert.IsType<AccountCreated>(Assert.Single(seeded.Events).Event).UserId;
    }
}
