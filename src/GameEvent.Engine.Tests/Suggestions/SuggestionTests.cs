using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Suggestions;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Suggestions;

/// <summary>
/// EV34: the suggestion box (SPEC «Предложка», D-527, task 5.14). A player of the season suggests a game, an event or an
/// item — a text, and for a game the fields of its card — up to <c>suggestions.perDay</c> a calendar day of
/// <c>season.timezone</c>. The admin approves or rejects with a comment. An approved game goes to the pool by the pool's
/// own command with the suggesting player as its author (the queue issues it from the approved suggestion: the pool is the
/// global log, D-119); an approved event or item only changes the status — the admin writes the content. Spectators do not
/// suggest. Written from the spec before the code: the contract is the one proposed in D-571.
/// </summary>
public class SuggestionTests
{
    private const int PerDay = 5;

    private static GameCard Card(string title = "Signalis", decimal? hours = 9) =>
        new(title, ["Horror"], hours, 2022, null, null, null, false);

    private static Ruleset WithSuggestions(Ruleset r, int perDay = PerDay) =>
        r with { Features = r.Features with { Suggestions = true }, Suggestions = new SuggestionRules { PerDay = perDay } };

    // The season starts at 2026-10-01 12:00 UTC: 15:00 in Moscow (UTC+3), whose day ends at 21:00 UTC
    private static Scenario Season(int perDay = PerDay)
    {
        var s = Scenario.New(WithSuggestions(TestRuleset.Create(), perDay));
        return s.WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя");
    }

    private static Scenario Submit(Scenario s, string player, SuggestionKind kind = SuggestionKind.Event, string text = "Ивент «Тишина»: неделю без звука", GameCard? game = null) =>
        s.Act(new SubmitSuggestion(s.PlayerId(player), kind, text, game));

    private static Guid Submitted(Scenario s, string player, SuggestionKind kind = SuggestionKind.Event, GameCard? game = null)
    {
        Submit(s, player, kind, game: game, text: kind == SuggestionKind.Game ? "Короткий хоррор, все хвалят" : "Ивент «Тишина»: неделю без звука");
        ScenarioAssert.Accepted(s);
        return Assert.Single(s.Last.Events.OfType<SuggestionSubmitted>()).SuggestionId;
    }

    // ---- S5.14-01: three kinds ----

    [Fact]
    public void A_player_suggests_a_game_an_event_and_an_item()
    {
        var s = Season();

        Submit(s, "Вася", SuggestionKind.Game, "Короткий хоррор, все хвалят", Card());
        var game = Assert.Single(s.LastEvents<SuggestionSubmitted>());
        Submit(s, "Вася", SuggestionKind.Event, "Ивент «Тишина»: неделю без звука");
        var @event = Assert.Single(s.LastEvents<SuggestionSubmitted>());
        Submit(s, "Вася", SuggestionKind.Item, "Предмет «Фонарик»: видишь следующую категорию");
        var item = Assert.Single(s.LastEvents<SuggestionSubmitted>());

        Assert.Equal(
            [SuggestionKind.Game, SuggestionKind.Event, SuggestionKind.Item],
            new[] { game, @event, item }.Select(e => e.Kind));
        Assert.All(new[] { game, @event, item }, e => Assert.Equal((s.PlayerId("Вася"), s.Player("Вася").UserId, s.Clock.UtcNow), (e.PlayerId, e.UserId, e.SubmittedAt)));
        Assert.Equal(Card(), game.Game);
        Assert.Null(@event.Game);
        Assert.Equal(3, new[] { game, @event, item }.Select(e => e.SuggestionId).Distinct().Count());

        Assert.All(s.State.Suggestions.Values, x => Assert.Equal(SuggestionStatus.Pending, x.Status));
        Assert.Equal("Предмет «Фонарик»: видишь следующую категорию", s.State.Suggestions[item.SuggestionId].Text);
        Assert.Equal(Card(), s.State.Suggestions[game.SuggestionId].Game);
    }

    [Theory]
    [InlineData(SuggestionKind.Event, "")]
    [InlineData(SuggestionKind.Event, "   ")]
    [InlineData(SuggestionKind.Item, "")]
    public void A_suggestion_without_text_is_refused(SuggestionKind kind, string text)
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", kind, text), RejectionCodes.SuggestionEmpty);
    }

    [Fact]
    public void A_game_suggestion_needs_the_card_with_a_title()
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", SuggestionKind.Game, "Хорошая игра", null), RejectionCodes.SuggestionGameRequired);
        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася", SuggestionKind.Game, "Хорошая игра", Card(title: " ")), RejectionCodes.SuggestionGameRequired);
    }

    // ---- S5.14-08: spectators (Н-21) ----

    [Fact]
    public void A_spectator_without_a_player_in_the_season_cannot_suggest()
    {
        var s = Season();
        var spectator = Guid.Parse("40000000-0000-0000-0000-0000000000aa");

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SubmitSuggestion(spectator, SuggestionKind.Event, "Ивент от зрителя", null)), RejectionCodes.PlayerUnknown);
    }

    // ---- S5.14-02: the daily limit by the season's timezone (Н-20) ----

    [Fact]
    public void The_suggestion_over_the_daily_limit_is_refused()
    {
        var s = Season();
        for (var i = 0; i < PerDay; i++)
        {
            var kind = new[] { SuggestionKind.Game, SuggestionKind.Event, SuggestionKind.Item }[i % 3];
            Submitted(s, "Вася", kind, kind == SuggestionKind.Game ? Card($"Игра {i}") : null);
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася"), RejectionCodes.SuggestionDailyLimit);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void The_limit_is_the_number_of_the_ruleset(int perDay)
    {
        var s = Season(perDay);
        for (var i = 0; i < perDay; i++)
        {
            Submitted(s, "Вася");
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася"), RejectionCodes.SuggestionDailyLimit);
    }

    [Fact]
    public void The_limit_is_per_author()
    {
        var s = Season();
        for (var i = 0; i < PerDay; i++)
        {
            Submitted(s, "Вася");
        }

        Submit(s, "Петя");

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Reviewed_suggestions_still_count_for_the_day()
    {
        var s = Season();
        for (var i = 0; i < PerDay; i++)
        {
            var id = Submitted(s, "Вася");
            s.Act(new ReviewSuggestion(id, Approve: false, "Уже есть похожий"));
            ScenarioAssert.Accepted(s);
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася"), RejectionCodes.SuggestionDailyLimit);
    }

    [Theory]
    [InlineData(8 * 60 + 59, false)] // 23:59 in Moscow, the same day
    [InlineData(9 * 60, true)] // 00:00 in Moscow: a new day, though the same day in UTC
    public void The_day_ends_at_midnight_of_the_season_timezone(int minutesLater, bool accepted)
    {
        var s = Season();
        for (var i = 0; i < PerDay; i++)
        {
            Submitted(s, "Вася");
        }

        s.Advance(TimeSpan.FromMinutes(minutesLater));
        Submit(s, "Вася");

        Assert.Equal(accepted, s.Last.IsAccepted);
        if (!accepted)
        {
            Assert.Equal(RejectionCodes.SuggestionDailyLimit, s.Last.Rejection!.Code);
        }
    }

    [Fact]
    public void The_next_day_opens_the_whole_limit_again()
    {
        var s = Season();
        for (var i = 0; i < PerDay; i++)
        {
            Submitted(s, "Вася");
        }

        s.Advance(TimeSpan.FromHours(9));
        for (var i = 0; i < PerDay; i++)
        {
            Submitted(s, "Вася");
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася"), RejectionCodes.SuggestionDailyLimit);
    }

    // ---- S5.14-03, S5.14-04: approval ----

    [Fact]
    public void An_approved_game_keeps_the_card_and_the_author_for_the_pool_command()
    {
        var s = Season();
        var id = Submitted(s, "Вася", SuggestionKind.Game, Card());

        s.Act(new ReviewSuggestion(id, Approve: true, null));

        ScenarioAssert.Accepted(s);
        var reviewed = Assert.Single(s.LastEvents<SuggestionReviewed>());
        Assert.Equal((id, SuggestionStatus.Approved, s.Clock.UtcNow), (reviewed.SuggestionId, reviewed.Status, reviewed.ReviewedAt));
        var suggestion = s.State.Suggestions[id];
        Assert.Equal((SuggestionStatus.Approved, SuggestionKind.Game, Card(), s.Player("Вася").UserId), (suggestion.Status, suggestion.Kind, suggestion.Game, suggestion.UserId));
    }

    [Fact]
    public void Approving_a_game_does_not_change_the_season_pool_by_itself()
    {
        // The pool is the global log (D-119): the season log records the approval, the pool command adds the game
        var s = Season();
        var id = Submitted(s, "Вася", SuggestionKind.Game, Card());

        s.Act(new ReviewSuggestion(id, Approve: true, "Добавил"));

        Assert.DoesNotContain(s.Last.Events, e => e is GameAdded);
    }

    [Theory]
    [InlineData(SuggestionKind.Event)]
    [InlineData(SuggestionKind.Item)]
    public void An_approved_event_or_item_only_changes_the_status(SuggestionKind kind)
    {
        var s = Season();
        var id = Submitted(s, "Вася", kind);
        var catalog = s.State.Catalog;
        var players = s.State.Players;

        s.Act(new ReviewSuggestion(id, Approve: true, "Напишу к следующей неделе"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(SuggestionReviewed)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(SuggestionStatus.Approved, s.State.Suggestions[id].Status);
        Assert.Equal(catalog, s.State.Catalog);
        Assert.Equal(players, s.State.Players);
    }

    // ---- S5.14-05: rejection with a comment (Н-28) ----

    [Fact]
    public void A_rejection_keeps_the_comment()
    {
        var s = Season();
        var id = Submitted(s, "Вася");

        s.Act(new ReviewSuggestion(id, Approve: false, "Слишком злой ивент"));

        ScenarioAssert.Accepted(s);
        var reviewed = Assert.Single(s.LastEvents<SuggestionReviewed>());
        Assert.Equal((SuggestionStatus.Rejected, "Слишком злой ивент"), (reviewed.Status, reviewed.Comment));
        Assert.Equal((SuggestionStatus.Rejected, "Слишком злой ивент"), (s.State.Suggestions[id].Status, s.State.Suggestions[id].Comment));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_rejection_without_a_comment_is_refused(string? comment)
    {
        var s = Season();
        var id = Submitted(s, "Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ReviewSuggestion(id, Approve: false, comment)), RejectionCodes.CommentRequired);
        Assert.Equal(SuggestionStatus.Pending, s.State.Suggestions[id].Status);
    }

    // ---- S5.14-06: a second review, an unknown suggestion ----

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_reviewed_suggestion_is_not_reviewed_again(bool first, bool second)
    {
        var s = Season();
        var id = Submitted(s, "Вася");
        s.Act(new ReviewSuggestion(id, first, "Решено"));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ReviewSuggestion(id, second, "Передумал")), RejectionCodes.SuggestionAlreadyReviewed);
    }

    [Fact]
    public void Reviewing_an_unknown_suggestion_is_refused()
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ReviewSuggestion(Guid.Parse("00000000-0000-0000-0000-0000005a99e5"), true, null)), RejectionCodes.SuggestionUnknown);
    }

    // ---- S5.14-07: the flag (EV35) ----

    [Fact]
    public void With_the_flag_off_suggesting_is_refused()
    {
        var s = Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Вася"), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void With_the_flag_switched_off_a_waiting_suggestion_is_not_reviewed_and_stays_waiting()
    {
        var s = Season();
        var id = Submitted(s, "Вася");
        s.WithRuleset(r => r with { Features = r.Features with { Suggestions = false } });

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ReviewSuggestion(id, true, null)), RejectionCodes.FeatureDisabled);
        Assert.Equal(SuggestionStatus.Pending, s.State.Suggestions[id].Status);

        s.WithRuleset(r => r with { Features = r.Features with { Suggestions = true } });
        s.Act(new ReviewSuggestion(id, true, null));
        ScenarioAssert.Accepted(s);
    }

    // ---- S5.14-10: after the deadline (Н-29) ----

    [Fact]
    public void Suggesting_and_reviewing_go_on_after_the_deadline()
    {
        // A suggestion changes no result, so the deadline does not stop it (D-516 is silent; default of the scenarios)
        var s = Season();
        s.Act(new SetSeasonDeadline(s.Clock.UtcNow + TimeSpan.FromHours(1)));
        s.Advance(TimeSpan.FromHours(1));
        s.Act(new ReachDeadline());
        Assert.Equal(SeasonStatus.Closing, s.State.Status);

        var id = Submitted(s, "Вася");
        s.Act(new ReviewSuggestion(id, false, "Уже после сезона"));

        ScenarioAssert.Accepted(s);
    }

    // ---- Undo (EV38) and replay ----

    [Fact]
    public void Undoing_a_waiting_suggestion_removes_it_and_frees_its_place_in_the_limit()
    {
        var s = Season(perDay: 1);
        var id = Submitted(s, "Вася");

        s.Act(new UndoCommand(s.LastCommandId, "Дубль"));

        ScenarioAssert.Accepted(s);
        Assert.False(s.State.Suggestions.ContainsKey(id));
        Submit(s, "Вася");
        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Undoing_a_review_returns_the_suggestion_to_waiting()
    {
        var s = Season();
        var id = Submitted(s, "Вася");
        s.Act(new ReviewSuggestion(id, false, "Не то"));

        s.Act(new UndoCommand(s.LastCommandId, "Отклонил по ошибке"));

        ScenarioAssert.Accepted(s);
        Assert.Equal((SuggestionStatus.Pending, (string?)null), (s.State.Suggestions[id].Status, s.State.Suggestions[id].Comment));
        Assert.Equal(SeasonEngine.Replay(s.EffectiveLog).Suggestions, s.State.Suggestions);
    }

    [Fact]
    public void Undoing_a_reviewed_suggestion_waits_for_the_review()
    {
        var s = Season();
        Submitted(s, "Вася");
        var submitted = s.LastCommandId;
        s.Act(new ReviewSuggestion(s.State.Suggestions.Keys.Single(), true, null));
        var reviewed = s.LastCommandId;

        var before = s.State;
        var log = s.Log.Count;
        s.Act(new UndoCommand(submitted, "Дубль"));

        ScenarioAssert.Rejected(s, before, log, RejectionCodes.UndoDependents);
        Assert.Contains(reviewed, s.Last.Rejection!.Related);
    }

    [Fact]
    public void Replaying_the_log_gives_the_same_suggestions()
    {
        var s = Season();
        var game = Submitted(s, "Вася", SuggestionKind.Game, Card());
        var @event = Submitted(s, "Петя");
        Submitted(s, "Петя", SuggestionKind.Item);
        s.Act(new ReviewSuggestion(game, true, null));
        s.Act(new ReviewSuggestion(@event, false, "Не в этом сезоне"));
        s.Advance(TimeSpan.FromDays(1));
        Submitted(s, "Вася");

        var replayed = SeasonEngine.Replay(s.Log);

        Assert.Equal(s.State, replayed);
        Assert.Equal(s.State.Suggestions, replayed.Suggestions);
        Assert.Equal(4, replayed.Suggestions.Count);
    }
}
