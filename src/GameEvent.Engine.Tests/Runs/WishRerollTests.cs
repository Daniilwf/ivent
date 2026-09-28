using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// «Реролл по желанию» (the owner's decision D-206, RGG 13; D-325): a game with a tag of <c>roll.wishRerollTags</c> (empty by
/// default) may be given up for free, like a tech reroll with the reason <see cref="TechRerollReason.Wish"/> — the game is
/// excluded for the player and a new roll follows. Only within the tech reroll window of the run (the admin cannot stretch
/// it either), and not when the roll's own filters imposed a genre (a zone's tags now; special rolls and events later):
/// the tags are fixed in the run's snapshot (<see cref="RunSnapshot.ImposedTags"/>). The list is
/// fixed at the roll too (<see cref="RunSnapshot.WishRerollTags"/>); tags compare ignoring case, like the wheel's.
/// </summary>
public class WishRerollTests
{
    private static Func<Ruleset, Ruleset> Listed(params string[] tags) =>
        r => r with { Roll = r.Roll with { WishRerollTags = [.. tags] } };

    /// <summary>One category «Any»; Persona is an RPG, the rest are Horror; 3 hours each.</summary>
    private static Scenario Season(Func<Ruleset, Ruleset>? rules = null, MapGraph? map = null)
    {
        var s = Scenario.New();
        if (rules is not null)
        {
            s.WithRuleset(rules);
        }

        if (map is not null)
        {
            s.WithMap(map);
        }

        return s.WithCategory("Any")
            .WithGame("Persona", 3, "Any", "RPG").WithGame("Silent Hill", 3, "Any", "Horror").WithGame("Fatal Frame", 3, "Any", "Horror")
            .WithGame("Dragon Quest", 3, "Any", "rpg")
            .WithPlayers("Вася", "Петя");
    }

    private static Scenario Playing(Scenario s, string title) => s.RollTitle("Вася", title).Start("Вася");

    private static Scenario Wish(Scenario s, bool byAdmin = false) =>
        s.Act(new TechReroll(s.PlayerId("Вася"), TechRerollReason.Wish, byAdmin ? "По просьбе игрока" : null, byAdmin));

    [Fact]
    public void Wish_reroll_of_a_listed_genre_is_free_excludes_the_game_and_rolls_again()
    {
        var s = Playing(Season(Listed("RPG")), "Persona");
        var run = s.Player("Вася").ActiveRunId!.Value;
        var persona = s.GameId("Persona");

        Wish(s);

        ScenarioAssert.Accepted(s);
        var rerolled = Assert.Single(s.LastEvents<RunTechRerolled>());
        Assert.Equal((run, TechRerollReason.Wish, false), (rerolled.RunId, rerolled.Reason, rerolled.ByAdmin));
        Assert.Equal(new GameExcluded(s.PlayerId("Вася"), persona, ExclusionReason.TechRerolled), Assert.Single(s.LastEvents<GameExcluded>()));
        Assert.Single(s.LastEvents<GameRolled>());
        Assert.Equal((0, 0, "start"), (s.Player("Вася").Points, s.Player("Вася").Coins, s.Player("Вася").CellId));
    }

    [Fact]
    public void Listed_tags_compare_ignoring_case()
    {
        var s = Playing(Season(Listed("RPG")), "Dragon Quest");

        Wish(s);

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Wish_reroll_of_a_game_without_a_listed_tag_is_refused()
    {
        var s = Playing(Season(Listed("RPG")), "Silent Hill");

        ScenarioAssert.RejectsWithoutChanges(s, x => Wish(x), RejectionCodes.WishRerollNotListed);
    }

    [Fact]
    public void Wish_reroll_with_the_default_empty_list_is_refused()
    {
        var s = Playing(Season(), "Persona");

        ScenarioAssert.RejectsWithoutChanges(s, x => Wish(x), RejectionCodes.WishRerollNotListed);
    }

    [Fact]
    public void Wish_reroll_after_the_tech_reroll_window_is_refused_even_for_the_admin()
    {
        var s = Playing(Season(Listed("RPG")), "Persona");
        s.Advance(TimeSpan.FromHours(s.Ruleset.Roll.TechRerollWindowHours) + TimeSpan.FromSeconds(1));

        ScenarioAssert.RejectsWithoutChanges(s, x => Wish(x), RejectionCodes.TechRerollWindowClosed);
        ScenarioAssert.RejectsWithoutChanges(s, x => Wish(x, byAdmin: true), RejectionCodes.TechRerollWindowClosed);
    }

    [Fact]
    public void Wish_reroll_at_the_end_of_the_window_is_allowed()
    {
        var s = Playing(Season(Listed("RPG")), "Persona");
        s.Advance(TimeSpan.FromHours(s.Ruleset.Roll.TechRerollWindowHours));

        Wish(s);

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void List_is_fixed_in_the_snapshot_at_the_roll()
    {
        // Listed after the roll: this run keeps the list of its roll (SPEC «Снапшот»)
        var s = Playing(Season(), "Persona");
        s.Act(new ChangeRuleset(Listed("RPG")(s.Ruleset)));
        ScenarioAssert.Accepted(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => Wish(x), RejectionCodes.WishRerollNotListed);
    }

    [Fact]
    public void List_taken_off_after_the_roll_still_lets_this_run_go()
    {
        var s = Playing(Season(Listed("RPG")), "Persona");
        Assert.Equal(["RPG"], s.State.Runs[s.Player("Вася").ActiveRunId!.Value].Snapshot.WishRerollTags);
        s.Act(new ChangeRuleset(Listed()(s.Ruleset)));
        ScenarioAssert.Accepted(s);

        Wish(s);

        ScenarioAssert.Accepted(s);
    }

    // ---- A genre imposed by the roll ----

    /// <summary>start → a → b → finish; start and a in a zone whose roll filter is <paramref name="filter"/>.</summary>
    private static MapGraph ZoneMap(GameFilterSpec filter) =>
        MapBuilder.New().Path("start", "a", "b", "finish").Zone(new ZoneDefinition { Id = "rpg-land", Name = "Земля RPG", RollFilter = filter }, "start", "a").Build();

    [Fact]
    public void Wish_reroll_is_refused_when_a_zone_imposed_the_genre()
    {
        var s = Playing(Season(Listed("RPG"), ZoneMap(new GameFilterSpec { Tags = ["RPG"] })), "Persona");
        var run = s.State.Runs[s.Player("Вася").ActiveRunId!.Value];
        Assert.Equal(["RPG"], run.Snapshot.ImposedTags);

        ScenarioAssert.RejectsWithoutChanges(s, x => Wish(x), RejectionCodes.WishRerollImposed);
    }

    [Fact]
    public void Any_imposed_genre_closes_the_wish_reroll_even_for_another_tag_of_the_game()
    {
        // The zone imposes Horror; Hellblade is Horror and RPG, RPG is listed: still no escape from the zone
        var s = Scenario.New().WithRuleset(Listed("RPG")).WithMap(ZoneMap(new GameFilterSpec { Tags = ["Horror"] })).WithCategory("Any")
            .WithGame("Hellblade", 3, "Any", "Horror", "RPG").WithGame("Persona", 3, "Any", "RPG").WithPlayers("Вася");
        s.RollTitle("Вася", "Hellblade").Start("Вася");

        ScenarioAssert.RejectsWithoutChanges(s, x => Wish(x), RejectionCodes.WishRerollImposed);
    }

    [Fact]
    public void The_roll_decides_the_imposed_genre_not_the_cell_the_player_stands_on_now()
    {
        // Rolled outside the zone, then moved into it: the roll imposed nothing
        var map = MapBuilder.New().Path("start", "a", "b", "finish")
            .Zone(new ZoneDefinition { Id = "rpg-land", Name = "Земля RPG", RollFilter = new GameFilterSpec { Tags = ["RPG"] } }, "a").Build();
        var s = Playing(Season(Listed("RPG"), map), "Persona");
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Перенос", CellId: "a"));
        ScenarioAssert.Accepted(s);

        Wish(s);

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Zone_filter_without_tags_imposes_no_genre()
    {
        var s = Playing(Season(Listed("RPG"), ZoneMap(new GameFilterSpec { MaxHours = 5 })), "Persona");
        Assert.Empty(s.State.Runs[s.Player("Вася").ActiveRunId!.Value].Snapshot.ImposedTags);

        Wish(s);

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Zone_filter_lifted_for_an_empty_pool_imposes_no_genre()
    {
        // The zone asks for Puzzle games, the pool has none: the filter is lifted and the roll is the ordinary one
        var s = Playing(Season(Listed("RPG"), ZoneMap(new GameFilterSpec { Tags = ["Puzzle"] })), "Persona");
        Assert.Empty(s.State.Runs[s.Player("Вася").ActiveRunId!.Value].Snapshot.ImposedTags);

        Wish(s);

        ScenarioAssert.Accepted(s);
    }

    [Fact]
    public void Wish_reroll_is_a_turn_of_the_playing_player()
    {
        var s = Season(Listed("RPG")).RollTitle("Вася", "Persona");

        ScenarioAssert.RejectsWithoutChanges(s, x => Wish(x), RejectionCodes.WrongPhase);
    }
}
