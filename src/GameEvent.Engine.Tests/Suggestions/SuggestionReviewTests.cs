using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Suggestions;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Suggestions;

/// <summary>
/// Found by the review of task 5.14 (D-579): the lengths of a suggestion, its trimming, the card checked by the pool's
/// rules at once, the season's calendar given by the host (the engine reads no time zone database) and the undo of the
/// author's joining waiting for their suggestions.
/// </summary>
public class SuggestionReviewTests
{
    private static GameCard Card(string title = "Signalis", string? note = null) =>
        new(title, ["Horror"], 9, 2022, null, null, note, false);

    private static Scenario Season(Func<Ruleset, Ruleset>? more = null)
    {
        var rules = TestRuleset.Create() with { Features = TestRuleset.Create().Features with { Suggestions = true }, Suggestions = new SuggestionRules { PerDay = 5 } };
        var s = Scenario.New(more is null ? rules : more(rules));
        return s.WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя");
    }

    private static Scenario Submit(Scenario s, string text, SuggestionKind kind = SuggestionKind.Event, GameCard? game = null) =>
        s.Act(new SubmitSuggestion(s.PlayerId("Вася"), kind, text, game));

    private static Guid Submitted(Scenario s)
    {
        Submit(s, "Ивент «Тишина»");
        ScenarioAssert.Accepted(s);
        return Assert.Single(s.LastEvents<SuggestionSubmitted>()).SuggestionId;
    }

    // ---- Lengths and trimming ----

    [Fact]
    public void A_text_of_the_limit_is_accepted()
    {
        var s = Season();

        Submit(s, new string('а', Limits.MaxSuggestionLength));

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void A_text_over_the_limit_is_refused()
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, new string('а', Limits.MaxSuggestionLength + 1)), RejectionCodes.SuggestionTooLong);
    }

    [Fact]
    public void The_limit_counts_the_trimmed_text()
    {
        var s = Season();

        Submit(s, "  " + new string('а', Limits.MaxSuggestionLength) + "  ");

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void The_text_and_the_title_are_stored_trimmed()
    {
        var s = Season();

        Submit(s, "  Короткий хоррор  ", SuggestionKind.Game, Card("  Signalis  "));

        ScenarioAssert.Accepted(s);
        var submitted = Assert.Single(s.LastEvents<SuggestionSubmitted>());
        Assert.Equal(("Короткий хоррор", "Signalis"), (submitted.Text, submitted.Game!.Title));
        Assert.Equal(submitted.Text, s.State.Suggestions[submitted.SuggestionId].Text);
    }

    [Fact]
    public void A_moderation_comment_of_the_limit_is_accepted()
    {
        var s = Season();
        var id = Submitted(s);

        s.Act(new ReviewSuggestion(id, false, new string('к', Limits.MaxCommentLength)));

        ScenarioAssert.Accepted(s);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_moderation_comment_over_the_limit_is_refused(bool approve)
    {
        var s = Season();
        var id = Submitted(s);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ReviewSuggestion(id, approve, new string('к', Limits.MaxCommentLength + 1))), RejectionCodes.CommentTooLong);
    }

    [Fact]
    public void The_moderation_comment_is_stored_trimmed()
    {
        var s = Season();
        var id = Submitted(s);

        s.Act(new ReviewSuggestion(id, false, "  Уже есть  "));

        Assert.Equal("Уже есть", s.State.Suggestions[id].Comment);
    }

    // ---- The card by the pool's rules ----

    public static TheoryData<string, GameCard> InvalidCards() => new()
    {
        { "a title over 200", Card(new string('t', GameCards.MaxTitleLength + 1)) },
        { "a note over 1000", Card(note: new string('n', GameCards.MaxNoteLength + 1)) },
        { "a condition over 1000", Card() with { CompletionCondition = new string('c', GameCards.MaxNoteLength + 1) } },
        { "too many tags", Card() with { Tags = [.. Enumerable.Range(0, GameCards.MaxTags + 1).Select(i => $"tag{i}")] } },
        { "a tag too long", Card() with { Tags = [new string('g', GameCards.MaxTagLength + 1)] } },
        { "hours not in half hours", Card() with { Hours = 1.3m } },
        { "hours over the ceiling", Card() with { Hours = GameCards.MaxHours + 1 } },
        { "a year before games", Card() with { Year = GameCards.MinYear - 1 } },
        { "a Steam id of letters", Card() with { SteamAppId = "abc" } },
    };

    [Theory]
    [MemberData(nameof(InvalidCards))]
    public void A_card_the_pool_would_refuse_is_refused_at_once(string what, GameCard card)
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, what, SuggestionKind.Game, card), RejectionCodes.SuggestionCardInvalid);
    }

    [Fact]
    public void A_card_of_the_limits_is_accepted_as_the_pool_stores_it()
    {
        var s = Season();
        var card = Card(new string('t', GameCards.MaxTitleLength), new string('n', GameCards.MaxNoteLength)) with { Tags = ["Horror", "horror", " "] };

        Submit(s, "", SuggestionKind.Game, card);

        ScenarioAssert.Accepted(s);
        Assert.Equal(card with { Tags = ["Horror"] }, Assert.Single(s.LastEvents<SuggestionSubmitted>()).Game);
    }

    // ---- The season's calendar comes from the host ----

    [Fact]
    public void An_unknown_season_timezone_refuses_the_suggestion_instead_of_counting_in_utc()
    {
        var s = Season(r => r with { Season = r.Season with { Timezone = "Mars/Olympus" } });

        ScenarioAssert.RejectsWithoutChanges(s, x => Submit(x, "Ивент «Тишина»"), RejectionCodes.SeasonTimezoneUnknown);
    }

    [Fact]
    public void Without_the_calendar_of_the_host_the_suggestion_is_refused()
    {
        var s = Season();

        var result = SeasonEngine.Execute(
            s.State, new SubmitSuggestion(s.PlayerId("Вася"), SuggestionKind.Event, "Ивент", null), s.Context() with { TimeZones = null });

        Assert.Equal(RejectionCodes.SeasonTimezoneUnknown, result.Rejection!.Code);
    }

    // ---- Undo of the author's joining ----

    [Fact]
    public void Undoing_the_joining_of_a_player_waits_for_their_suggestions()
    {
        var s = Season();
        var dasha = Guid.Parse("0d000000-0000-0000-0000-000000000001");
        s.Act(new AddSeasonPlayer(dasha, Guid.Parse("0e000000-0000-0000-0000-000000000001"), "Даша"));
        ScenarioAssert.Accepted(s);
        var joined = s.LastCommandId;
        s.Act(new SubmitSuggestion(dasha, SuggestionKind.Event, "Ивент «Тишина»", null));
        ScenarioAssert.Accepted(s);
        var submitted = s.LastCommandId;

        var before = s.State;
        var log = s.Log.Count;
        s.Act(new UndoCommand(joined, "Добавил не того"));

        ScenarioAssert.Rejected(s, before, log, RejectionCodes.UndoDependents);
        Assert.Contains(submitted, s.Last.Rejection!.Related);

        s.Act(new UndoCommand(submitted, "Не её предложение"));
        ScenarioAssert.Accepted(s);
        s.Act(new UndoCommand(joined, "Добавил не того"));
        ScenarioAssert.Accepted(s);
        Assert.False(s.State.Players.ContainsKey(dasha));
    }

    [Fact]
    public void A_suggestion_still_does_not_hold_back_the_undo_of_the_authors_turn()
    {
        var s = Season();
        s.RollTitle("Вася", "Silent Hill");
        var rolled = s.LastCommandId;
        Submitted(s);

        s.Act(new UndoCommand(rolled, "Ролл по ошибке"));

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Suggestions_of_one_author_do_not_hold_back_each_others_undo()
    {
        var s = Season();
        var first = Submitted(s);
        var submitted = s.LastCommandId;
        var second = Submitted(s);

        s.Act(new UndoCommand(submitted, "Дубль"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([second], s.State.Suggestions.Keys);
        Assert.NotEqual(first, second);
    }
}
