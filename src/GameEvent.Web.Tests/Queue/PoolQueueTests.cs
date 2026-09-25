using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Queue;

/// <summary>
/// The pool in the queue (SPEC «Пул игр», «Дубли», «Удаление мягкое»; D-119): cards are checked and stored trimmed, the same
/// title never twice, an alike one only when confirmed, soft deletion and restoring, the category wheel.
/// </summary>
public sealed class PoolQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GameCard Card(string title, params string[] tags) => new(title, [.. tags], null, null, null, null, null, IsCoop: false);

    [Fact]
    public async Task A_game_is_added_with_its_card_trimmed_and_its_author()
    {
        await using var h = await QueueHarness.StartAsync();
        var author = Guid.NewGuid();

        var outcome = await SendAsync(h, new AddGame(new GameCard("  Dead Space ", [" Horror", "horror", "Sci-fi ", " "], 12m, 2008, " 17470 ", null, "  любая концовка ", IsCoop: false), author, Force: false));

        var added = Assert.IsType<GameAdded>(Assert.Single(outcome.Events).Event);
        Assert.Equal(new GameCard("Dead Space", ["Horror", "Sci-fi"], 12m, 2008, "17470", null, "любая концовка", IsCoop: false), added.Card);
        await using var db = h.NewDb();
        var game = await db.Games.SingleAsync(g => g.Id == added.GameId, Ct);
        Assert.Equal(("Dead Space", author, 2008, "17470"), (game.Title, game.AuthorId!.Value, game.Year!.Value, game.SteamAppId));
    }

    [Fact]
    public async Task A_repeated_request_adds_one_game()
    {
        await using var h = await QueueHarness.StartAsync();
        var commandId = Guid.NewGuid();

        var first = await h.Bus.SendAsync(new CommandEnvelope(commandId, Guid.Empty, new AddGame(Card("Dead Space", "Horror"), null, false), null), Ct);
        var again = await h.Bus.SendAsync(new CommandEnvelope(commandId, Guid.Empty, new AddGame(Card("Dead Space", "Horror"), null, false), null), Ct);

        Assert.True(again.IsDuplicate);
        Assert.Equal(first.Events.Single().Event, again.Events.Single().Event);
        await using var db = h.NewDb();
        Assert.Single(await db.Games.Where(g => g.Title == "Dead Space").ToListAsync(Ct));
    }

    [Theory]
    [InlineData("Silent Hill")]
    [InlineData("silent hill")]
    [InlineData("  SILENT HILL ")]
    public async Task The_same_title_is_never_added_twice_even_when_confirmed(string title)
    {
        await using var h = await QueueHarness.StartAsync();

        var outcome = await SendAsync(h, new AddGame(Card(title, "Horror"), null, Force: true));

        Assert.Equal(PoolRules.Duplicate, outcome.Rejection!.Code);
    }

    [Fact]
    public async Task An_alike_title_needs_the_authors_confirmation()
    {
        await using var h = await QueueHarness.StartAsync();
        Assert.True((await SendAsync(h, new AddGame(Card("Dice Fold", "Puzzle"), null, false))).IsAccepted);

        var refused = await SendAsync(h, new AddGame(Card("Dice & Fold", "Puzzle"), null, Force: false));
        var confirmed = await SendAsync(h, new AddGame(Card("Dice & Fold", "Puzzle"), null, Force: true));

        Assert.Equal(PoolRules.Similar, refused.Rejection!.Code);
        Assert.Contains("«Dice Fold»", refused.Rejection.Detail, StringComparison.Ordinal);
        Assert.True(confirmed.IsAccepted);
    }

    [Fact]
    public async Task A_deleted_game_does_not_hold_its_title()
    {
        await using var h = await QueueHarness.StartAsync();
        var gameId = await AddAsync(h, "Dead Space");
        Assert.True((await SendAsync(h, new DeleteGame(gameId))).IsAccepted);

        Assert.True((await SendAsync(h, new AddGame(Card("Dead Space", "Horror"), null, false))).IsAccepted);

        // The old one cannot come back while the new one holds the title
        Assert.Equal(PoolRules.Duplicate, (await SendAsync(h, new RestoreGame(gameId))).Rejection!.Code);
    }

    [Theory]
    [MemberData(nameof(InvalidCards))]
    public async Task An_invalid_card_is_refused(GameCard card)
    {
        await using var h = await QueueHarness.StartAsync();

        Assert.Equal(PoolRules.CardInvalid, (await SendAsync(h, new AddGame(card, null, false))).Rejection!.Code);
    }

    public static TheoryData<GameCard> InvalidCards() => new()
    {
        Card(""),
        Card("   "),
        Card(new string('a', PoolRules.MaxTitleLength + 1)),
        Card("Game", [.. Enumerable.Range(0, PoolRules.MaxTags + 1).Select(i => $"tag{i}")]),
        Card("Game", new string('t', PoolRules.MaxTagLength + 1)),
        Card("Game") with { Hours = 0 },
        Card("Game") with { Hours = 0.3m },
        Card("Game") with { Hours = PoolRules.MaxHours + 1 },
        Card("Game") with { Year = 1900 },
        Card("Game") with { SteamAppId = "abc" },
        Card("Game") with { SteamAppId = "1234567890123" },
        Card("Game") with { Note = new string('n', PoolRules.MaxNoteLength + 1) },
    };

    [Fact]
    public async Task A_cover_that_is_not_stored_is_refused()
    {
        await using var h = await QueueHarness.StartAsync();

        Assert.Equal(PoolRules.CoverUnknown, (await SendAsync(h, new AddGame(Card("Game") with { CoverFileId = Guid.NewGuid() }, null, false))).Rejection!.Code);
    }

    [Fact]
    public async Task A_stored_cover_is_taken()
    {
        await using var h = await QueueHarness.StartAsync();
        var seeded = await SendAsync(h, new Infrastructure.Accounts.SeedAccount("admin", "Админ", Infrastructure.Accounts.Role.Admin, "пароль-админа-1"));
        var owner = Assert.IsType<Engine.Accounts.AccountCreated>(Assert.Single(seeded.Events).Event).UserId;
        var file = Guid.NewGuid();
        Assert.True((await SendAsync(h, new RecordFile(file, owner, FileNames.Webp, 10, 10, 10, 1, int.MaxValue))).IsAccepted);

        var outcome = await SendAsync(h, new AddGame(Card("Game") with { CoverFileId = file }, owner, false));

        Assert.Equal(file, Assert.IsType<GameAdded>(Assert.Single(outcome.Events).Event).Card.CoverFileId);
    }

    [Fact]
    public async Task The_admin_changes_a_card()
    {
        await using var h = await QueueHarness.StartAsync();
        var gameId = await AddAsync(h, "Dead Space");

        var outcome = await SendAsync(h, new ChangeGame(gameId, new GameCard("Dead Space", ["Horror"], 11.5m, 2008, null, null, "Условие", IsCoop: true), false));

        Assert.Equal(new GameChanged(gameId, new GameCard("Dead Space", ["Horror"], 11.5m, 2008, null, null, "Условие", IsCoop: true)), Assert.Single(outcome.Events).Event);
        await using var db = h.NewDb();
        Assert.Equal((11.5m, true), ((await db.Games.SingleAsync(g => g.Id == gameId, Ct)).Hours!.Value, (await db.Games.SingleAsync(g => g.Id == gameId, Ct)).IsCoop));
    }

    [Fact]
    public async Task A_change_keeps_the_title_rules_and_refuses_nothing_to_change()
    {
        await using var h = await QueueHarness.StartAsync();
        var gameId = await AddAsync(h, "Dead Space");
        await AddAsync(h, "Outlast");

        Assert.Equal(PoolRules.NothingToChange, (await SendAsync(h, new ChangeGame(gameId, Card("Dead Space", "Horror"), false))).Rejection!.Code);
        Assert.Equal(PoolRules.Duplicate, (await SendAsync(h, new ChangeGame(gameId, Card("outlast", "Horror"), true))).Rejection!.Code);
        Assert.Equal(PoolRules.Unknown, (await SendAsync(h, new ChangeGame(Guid.NewGuid(), Card("X", "Horror"), false))).Rejection!.Code);

        // Its own title with another case is not a duplicate of itself
        Assert.True((await SendAsync(h, new ChangeGame(gameId, Card("DEAD SPACE", "Horror"), false))).IsAccepted);
    }

    [Fact]
    public async Task Delete_and_restore_are_soft_and_checked()
    {
        await using var h = await QueueHarness.StartAsync();
        var gameId = await AddAsync(h, "Dead Space");

        Assert.Equal(PoolRules.NotDeleted, (await SendAsync(h, new RestoreGame(gameId))).Rejection!.Code);
        Assert.True((await SendAsync(h, new DeleteGame(gameId))).IsAccepted);
        Assert.Equal(PoolRules.Deleted, (await SendAsync(h, new DeleteGame(gameId))).Rejection!.Code);
        await using (var db = h.NewDb())
        {
            Assert.True((await db.Games.SingleAsync(g => g.Id == gameId, Ct)).IsDeleted);
        }

        Assert.True((await SendAsync(h, new RestoreGame(gameId))).IsAccepted);
        Assert.Equal(PoolRules.Unknown, (await SendAsync(h, new DeleteGame(Guid.NewGuid()))).Rejection!.Code);
    }

    [Fact]
    public async Task The_category_wheel_is_kept()
    {
        await using var h = await QueueHarness.StartAsync();

        Assert.Equal(new CategorySet("Racing", 2), Assert.Single((await SendAsync(h, new SetCategory(" Racing ", 2))).Events).Event);
        Assert.Equal(PoolRules.NothingToChange, (await SendAsync(h, new SetCategory("Racing", 2))).Rejection!.Code);
        Assert.True((await SendAsync(h, new SetCategory("Racing", 5))).IsAccepted);
        Assert.Equal(PoolRules.CategoryInvalid, (await SendAsync(h, new SetCategory("Racing", 0))).Rejection!.Code);
        Assert.Equal(PoolRules.CategoryInvalid, (await SendAsync(h, new SetCategory(" ", 3))).Rejection!.Code);
        Assert.True((await SendAsync(h, new RemoveCategory("Racing"))).IsAccepted);
        Assert.Equal(PoolRules.CategoryUnknown, (await SendAsync(h, new RemoveCategory("Racing"))).Rejection!.Code);
        await using var db = h.NewDb();
        Assert.DoesNotContain(await db.Categories.ToListAsync(Ct), c => c.Name == "Racing");
    }

    [Theory]
    [InlineData("Dice Fold", "Dice & Fold", true)]
    [InlineData("Dice Fold", "dice-fold", true)]
    [InlineData("Silent Hill", "Silent Hill 2", true)]
    [InlineData("Portal", "Portal 2", true)]
    [InlineData("Doom", "Doom 3", false)]
    [InlineData("Hades", "Hades", true)]
    [InlineData("Celeste", "Cuphead", false)]
    [InlineData("The Witcher 3", "The Witcher 2", true)]
    [InlineData("Limbo", "Inside", false)]
    [InlineData("!!!", "???", false)]
    public void Alike_titles_are_told(string a, string b, bool alike) => Assert.Equal(alike, PoolRules.IsAlike(a, b));

    private static Task<CommandOutcome> SendAsync(QueueHarness h, ICommand command) =>
        h.Bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, command, AuthorId: null), Ct);

    private static async Task<Guid> AddAsync(QueueHarness h, string title)
    {
        var outcome = await SendAsync(h, new AddGame(Card(title, "Horror"), null, false));
        return Assert.IsType<GameAdded>(Assert.Single(outcome.Events).Event).GameId;
    }
}
