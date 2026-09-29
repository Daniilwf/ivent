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
    [InlineData("Silent   Hill")]
    [InlineData("Silent\tHill")]
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
    public async Task A_deleted_game_holds_its_title_with_its_reason()
    {
        // D-208 (RGG 15) replaced D-119's «a deleted game does not hold its title»: the admin's reason is told instead
        await using var h = await QueueHarness.StartAsync();
        var gameId = await AddAsync(h, "Dead Space");
        Assert.True((await SendAsync(h, new DeleteGame(gameId, "  Дубль Dead Space Remake "))).IsAccepted);

        var added = await SendAsync(h, new AddGame(Card("dead  space", "Horror"), null, Force: true));
        var renamed = await SendAsync(h, new ChangeGame(await AddAsync(h, "Outlast"), Card("Dead Space", "Horror"), Force: true));

        Assert.Equal(PoolRules.Removed, added.Rejection!.Code);
        Assert.Contains("Дубль Dead Space Remake", added.Rejection.Detail, StringComparison.Ordinal);
        Assert.Equal(PoolRules.Removed, renamed.Rejection!.Code);
        await using var db = h.NewDb();
        Assert.Equal("Дубль Dead Space Remake", (await db.Games.SingleAsync(g => g.Id == gameId, Ct)).DeletionReason);
    }

    [Fact]
    public async Task A_deleted_game_comes_back_only_while_no_game_holds_its_title()
    {
        await using var h = await QueueHarness.StartAsync();
        var gameId = await AddAsync(h, "Dead Space");
        Assert.True((await SendAsync(h, new DeleteGame(gameId, "Дубль"))).IsAccepted);

        // A pool from before D-208 may hold the title again: the old one cannot come back beside it
        await using (var db = h.NewDb())
        {
            db.Games.Add(new GameRecord { Id = Guid.NewGuid(), Title = "Dead Space", TagsJson = "[]" });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(PoolRules.Duplicate, (await SendAsync(h, new RestoreGame(gameId))).Rejection!.Code);
    }

    [Fact]
    public async Task A_game_in_the_pool_is_told_before_one_taken_out_under_the_same_title()
    {
        // D-241: a pool from before D-208 may hold the title twice; the game in the pool is what the author is told about
        await using var h = await QueueHarness.StartAsync();
        var gameId = await AddAsync(h, "Dead Space");
        Assert.True((await SendAsync(h, new DeleteGame(gameId, "Дубль"))).IsAccepted);
        await using (var db = h.NewDb())
        {
            db.Games.Add(new GameRecord { Id = Guid.NewGuid(), Title = "Dead Space", TagsJson = "[]" });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(PoolRules.Duplicate, (await SendAsync(h, new AddGame(Card("Dead Space", "Horror"), null, Force: true))).Rejection!.Code);
    }

    [Fact]
    public async Task A_deletion_without_a_reason_holds_no_title()
    {
        // D-241: a game deleted before D-208 or a season import's placeholder was not taken out by the admin with a reason
        await using var h = await QueueHarness.StartAsync();
        await using (var db = h.NewDb())
        {
            db.Games.Add(new GameRecord { Id = Guid.NewGuid(), Title = "Dead Space", TagsJson = "[]", IsDeleted = true });
            await db.SaveChangesAsync(Ct);
        }

        Assert.True((await SendAsync(h, new AddGame(Card("Dead Space", "Horror"), null, Force: false))).IsAccepted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_deletion_needs_a_reason(string reason)
    {
        await using var h = await QueueHarness.StartAsync();
        var gameId = await AddAsync(h, "Dead Space");

        Assert.Equal(PoolRules.ReasonInvalid, (await SendAsync(h, new DeleteGame(gameId, reason))).Rejection!.Code);
        Assert.Equal(PoolRules.ReasonInvalid, (await SendAsync(h, new DeleteGame(gameId, new string('r', PoolRules.MaxDeletionReasonLength + 1)))).Rejection!.Code);
        await using var db = h.NewDb();
        Assert.False((await db.Games.SingleAsync(g => g.Id == gameId, Ct)).IsDeleted);
    }

    [Fact]
    public async Task A_restore_clears_the_reason()
    {
        await using var h = await QueueHarness.StartAsync();
        var gameId = await AddAsync(h, "Dead Space");
        Assert.True((await SendAsync(h, new DeleteGame(gameId, "Дубль"))).IsAccepted);

        Assert.True((await SendAsync(h, new RestoreGame(gameId))).IsAccepted);

        await using var db = h.NewDb();
        Assert.Null((await db.Games.SingleAsync(g => g.Id == gameId, Ct)).DeletionReason);
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
        Assert.True((await SendAsync(h, new DeleteGame(gameId, "Дубль"))).IsAccepted);
        Assert.Equal(PoolRules.Deleted, (await SendAsync(h, new DeleteGame(gameId, "Дубль"))).Rejection!.Code);
        await using (var db = h.NewDb())
        {
            Assert.True((await db.Games.SingleAsync(g => g.Id == gameId, Ct)).IsDeleted);
        }

        Assert.True((await SendAsync(h, new RestoreGame(gameId))).IsAccepted);
        Assert.Equal(PoolRules.Unknown, (await SendAsync(h, new DeleteGame(Guid.NewGuid(), "Дубль"))).Rejection!.Code);
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

    [Fact]
    public async Task A_title_in_another_unicode_form_is_the_same_title()
    {
        await using var h = await QueueHarness.StartAsync();
        Assert.True((await SendAsync(h, new AddGame(Card("Pokémon Snap", "Puzzle"), null, false))).IsAccepted);

        // «é» composed above, «e» and a combining acute below: they look the same, they are the same
        var outcome = await SendAsync(h, new AddGame(Card("Pokémon Snap", "Puzzle"), null, Force: true));

        Assert.Equal(PoolRules.Duplicate, outcome.Rejection!.Code);
    }

    [Fact]
    public async Task A_title_is_stored_in_one_form_with_single_spaces()
    {
        await using var h = await QueueHarness.StartAsync();

        var outcome = await SendAsync(h, new AddGame(Card("  Pokémon   Snap ", "Puzzle"), null, false));

        Assert.Equal("Pokémon Snap", Assert.IsType<GameAdded>(Assert.Single(outcome.Events).Event).Card.Title);
    }

    [Fact]
    public async Task The_card_limits_are_inclusive()
    {
        await using var h = await QueueHarness.StartAsync();
        var edge = new GameCard(new string('a', PoolRules.MaxTitleLength), [new string('t', PoolRules.MaxTagLength)], PoolRules.MaxHours, PoolRules.MaxYear, "123456789012", null, new string('n', PoolRules.MaxNoteLength), IsCoop: false, new string('c', PoolRules.MaxNoteLength));

        Assert.True((await SendAsync(h, new AddGame(edge, null, false))).IsAccepted);
        Assert.True((await SendAsync(h, new AddGame(Card("Low edge") with { Hours = PoolRules.MinHours, Year = PoolRules.MinYear }, null, false))).IsAccepted);
    }

    [Fact]
    public async Task A_change_to_an_alike_title_needs_the_confirmation_too()
    {
        await using var h = await QueueHarness.StartAsync();
        var gameId = await AddAsync(h, "Dead Space");

        Assert.Equal(PoolRules.Similar, (await SendAsync(h, new ChangeGame(gameId, Card("Silent Hills", "Horror"), false))).Rejection!.Code);
        Assert.True((await SendAsync(h, new ChangeGame(gameId, Card("Silent Hills", "Horror"), true))).IsAccepted);
    }

    [Fact]
    public async Task A_game_comes_back_next_to_an_alike_title()
    {
        await using var h = await QueueHarness.StartAsync();
        var gameId = await AddAsync(h, "Dead Space");
        Assert.True((await SendAsync(h, new DeleteGame(gameId, "Дубль"))).IsAccepted);
        Assert.True((await SendAsync(h, new AddGame(Card("Dead Space 2", "Horror"), null, Force: true))).IsAccepted);

        Assert.True((await SendAsync(h, new RestoreGame(gameId))).IsAccepted);
    }

    [Fact]
    public async Task Categories_are_one_whatever_the_case()
    {
        await using var h = await QueueHarness.StartAsync();

        // The harness has «Horror» (weight 2): «horror» changes its weight, not a second category
        Assert.Equal(new CategorySet("Horror", 5), Assert.Single((await SendAsync(h, new SetCategory("horror", 5))).Events).Event);
        Assert.Equal(new CategoryRemoved("Horror"), Assert.Single((await SendAsync(h, new RemoveCategory("HORROR"))).Events).Event);
        await using var db = h.NewDb();
        Assert.DoesNotContain(await db.Categories.ToListAsync(Ct), c => string.Equals(c.Name, "horror", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_completion_condition_is_kept_apart_from_the_note()
    {
        await using var h = await QueueHarness.StartAsync();

        var outcome = await SendAsync(h, new AddGame(Card("Tetris Effect", "Puzzle") with { Note = "Челлендж: без удержания", CompletionCondition = " 100 линий " }, null, false));

        var card = Assert.IsType<GameAdded>(Assert.Single(outcome.Events).Event).Card;
        Assert.Equal(("Челлендж: без удержания", "100 линий"), (card.Note, card.CompletionCondition));
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
