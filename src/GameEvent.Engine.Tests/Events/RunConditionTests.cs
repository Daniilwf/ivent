using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// Conditions of a run (task 5.5; SPEC «Условия от ивентов» — «пройди на харде» is seen on the run's page; unmet, by default
/// the run counts without dice but with coins; «Противоречащие условия» — the more prior one applies, the other waits for the
/// next game; «Сложность засчитывается по пруфу»; «Каждый кубик хранится отдельно»; D-505, D-506, D-507, D-546 (2), D-405;
/// EV9, EV10, EV11). <c>modifyNextRoll.runCondition</c> (<c>difficultyAtLeast</c> and/or <c>difficultyAtMost</c>) waits for
/// the next roll as its own roll change, is fixed in the run's snapshot at the roll with the policy of the rules then
/// (<c>reward.unmetConditionPolicy</c>), leaves the waiting list when the game starts, and is checked at the completion by the
/// declared difficulty (<see cref="RunConditionChecked"/> after <c>RunCompleted</c>, before <c>CompletionRolled</c>). Unmet
/// under <c>noDiceKeepCoins</c>: no throw at all, no points, no move, no difficulty event; coins by hours as usual. An admin
/// difficulty change checks it again both ways with one difference in points and position; «условие неприменимо»
/// (<see cref="WaiveRunCondition"/>) counts it met and passes it on to the next roll.
/// Pinned test ruleset with events and items on: 3 hours = one die; easy d2, normal d4, hard d6, extreme d6 with a good
/// event; coins 1 per hour, at least 3. Line map start → c1 … c12 → finish. Alan Wake is 6 hours (two dice), the rest 3.
/// </summary>
public class RunConditionTests
{
    private const string Comment = "По пруфу";

    private static readonly (string Title, decimal Hours)[] s_games =
        [("Alan Wake", 6), ("Silent Hill", 3), ("Fatal Frame", 3), ("Siren", 3)];

    /// <summary>Items that put a condition on the owner's next run, as content JSON (parsed only when a test needs them).</summary>
    private static readonly Dictionary<string, string> s_json = new(StringComparer.Ordinal)
    {
        ["vow-hard"] = Vow("vow-hard", """{ "difficultyAtLeast": "hard" }"""),
        ["vow-normal"] = Vow("vow-normal", """{ "difficultyAtLeast": "normal" }"""),
        ["vow-extreme"] = Vow("vow-extreme", """{ "difficultyAtLeast": "extreme" }"""),
        ["vow-easy"] = Vow("vow-easy", """{ "difficultyAtMost": "easy" }"""),
        ["vow-at-most-hard"] = Vow("vow-at-most-hard", """{ "difficultyAtMost": "hard" }"""),
        ["vow-normal-to-hard"] = Vow("vow-normal-to-hard", """{ "difficultyAtLeast": "normal", "difficultyAtMost": "hard" }"""),
        ["more-dice"] = """
            {
              "id": "more-dice", "kind": "item", "name": "Лишний кубик", "description": "В следующем броске на один кубик больше.",
              "rarity": "common", "price": 10, "window": "anytime", "hostile": false, "stackable": true,
              "effect": { "target": { "selector": "self" }, "actions": [{ "type": "modifyDice", "when": "next", "stage": "count", "value": 1 }] }
            }
            """,
    };

    private static readonly string[] s_vows = ["vow-hard", "vow-normal", "vow-extreme", "vow-easy", "vow-at-most-hard", "vow-normal-to-hard", "more-dice"];

    private static string Vow(string id, string condition) => $$"""
        {
          "id": "{{id}}", "kind": "item", "name": "Обет {{id}}", "description": "Условие на следующее прохождение.",
          "rarity": "common", "price": 10, "window": "anytime", "hostile": false, "stackable": true,
          "effect": { "target": { "selector": "self" }, "actions": [{ "type": "modifyNextRoll", "runCondition": {{condition}} }] }
        }
        """;

    private static ObjectDefinition Object(string id) =>
        s_json.TryGetValue(id, out var json) ? ContentJson.Parse<ObjectDefinition>(json) : EventSetup.Object(id);

    /// <summary>
    /// The vows, «Даже не вспотел» (CONTENT.md event 5), a bad-deck sack, the «Сглаз» onto another; the good deck gives 2 coins
    /// (so a wrong difficulty draw shows), the bad deck holds <paramref name="badEvent"/>.
    /// </summary>
    internal static ContentPack Pack(string badEvent = "coins-minus-3") => new()
    {
        Objects =
        [
            .. new[] { "not-even-sweating", "sack-bad", "jinx", "coins-plus-2", "coins-minus-3" }.Concat(s_vows)
                .Distinct(StringComparer.Ordinal).Select(Object).OrderBy(o => o.Id, StringComparer.Ordinal),
        ],
        Wheels = [EventSetup.Deck(EventKind.Bad, badEvent), EventSetup.Deck(EventKind.Good, "coins-plus-2")],
    };

    internal static Scenario Season(
        ContentPack? pack = null, Func<Ruleset, Ruleset>? rules = null, (string Title, decimal Hours)[]? games = null, int seed = 42)
    {
        var ruleset = TestRuleset.Create().WithEvents();
        var s = Scenario.New(rules is null ? ruleset : rules(ruleset), seed).WithMap(EventSetup.Line()).WithCategory("Horror");
        foreach (var (title, hours) in games ?? s_games)
        {
            s.WithGame(title, hours, "Horror");
        }

        s.WithPlayers("Вася", "Петя");
        return s.WithContent(pack ?? Pack());
    }

    private static Ruleset WithChallenges(Ruleset r) => r with { Features = r.Features with { Challenges = true } };

    private static Ruleset Policy(Ruleset r, UnmetConditionPolicy policy) =>
        r with { Reward = r.Reward with { UnmetConditionPolicy = policy } };

    private static ConditionSpec AtLeast(Difficulty d) => new() { DifficultyAtLeast = d };

    private static ConditionSpec AtMost(Difficulty d) => new() { DifficultyAtMost = d };

    /// <summary>The player gives himself each item of <paramref name="ids"/> and uses it.</summary>
    private static Scenario Vowed(Scenario s, string player, params string[] ids)
    {
        foreach (var id in ids)
        {
            s.Give(player, id);
            s.Used(player, id);
        }

        return s;
    }

    /// <summary>The player rolls <paramref name="title"/> (any game when null) and starts it; returns the run.</summary>
    private static Guid Playing(Scenario s, string? title = "Alan Wake", string player = "Вася")
    {
        (title is null ? s.Roll(player) : s.RollTitle(player, title)).Start(player);
        return s.Player(player).ActiveRunId!.Value;
    }

    /// <summary>Вася plays <paramref name="title"/> and completes it on <paramref name="difficulty"/> with <paramref name="dice"/> scripted.</summary>
    private static Guid Completed(Scenario s, Difficulty difficulty, int[] dice, string? title = "Alan Wake", bool challenge = false)
    {
        var runId = Playing(s, title);
        s.NextRandom(dice).Complete("Вася", difficulty, challengeDone: challenge);
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return runId;
    }

    /// <summary>
    /// Scripts a 0 for the next random value: no die shows 0, so a throw in the next command fails at once with
    /// «outside the requested range», and a draw would consume it. The 0 is left for the next roll's category wheel.
    /// </summary>
    private static Scenario NoThrowExpected(Scenario s) => s.NextRandom(0);

    private static RunConditionChecked Check(Scenario s) => Assert.Single(s.LastEvents<RunConditionChecked>());

    private static string[] Forward(int from, int to) => [.. Enumerable.Range(from + 1, to - from).Select(i => $"c{i}")];

    private static string[] BackToStart(int from) => [.. Enumerable.Range(1, from - 1).Reverse().Select(i => $"c{i}"), "start"];

    // ---- Content (CONTENT.md «Условия и фильтры», event 5; D-506) ----

    [Fact]
    public void The_not_even_sweating_example_publishes_with_events_on_and_not_without()
    {
        var sweating = EconomyScenario.Examples()["not-even-sweating"];
        var pack = new ContentPack { Objects = [sweating], Wheels = [EventSetup.Deck(EventKind.Bad, "not-even-sweating")] };

        Assert.Empty(ContentPublishing.Check(pack, events: true));
        Assert.NotEmpty(ContentPublishing.Check(new ContentPack { Objects = [sweating] }, events: false));
    }

    [Fact]
    public void A_run_condition_takes_difficulty_at_most_alone_or_with_at_least()
    {
        var easy = Assert.IsType<ModifyNextRollAction>(Assert.Single(Object("vow-easy").Effect!.Actions)).RunCondition!;
        var both = Assert.IsType<ModifyNextRollAction>(Assert.Single(Object("vow-normal-to-hard").Effect!.Actions)).RunCondition!;

        Assert.Equal(((Difficulty?)null, (Difficulty?)Difficulty.Easy), (easy.DifficultyAtLeast, easy.DifficultyAtMost));
        Assert.Equal(((Difficulty?)Difficulty.Normal, (Difficulty?)Difficulty.Hard), (both.DifficultyAtLeast, both.DifficultyAtMost));
        Assert.Empty(ContentPublishing.Check(Pack(), events: true));
    }

    [Theory]
    [InlineData("""{ "game": { "tags": ["Horror"] } }""")]
    [InlineData("""{ "difficultyAtLeast": "hard", "game": { "maxHours": 10 } }""")]
    [InlineData("""{ "stat": "runHours", "gte": 5 }""")]
    [InlineData("""{ "difficultyAtLeast": "hard", "difficultyAtMost": "easy" }""")]
    [InlineData("""{ "difficultyAtLeast": "extreme", "difficultyAtMost": "hard" }""")]
    public void A_game_a_stat_or_crossed_bounds_in_a_run_condition_are_errors_at_the_run_condition(string condition)
    {
        // A game filter goes in «filter», not in the condition of the run (D-506); «not lower» above «not higher» never holds
        var pack = new ContentPack { Objects = [ContentJson.Parse<ObjectDefinition>(Vow("bad-vow", condition))] };

        var errors = ContentPublishing.Check(pack, events: true);

        Assert.Contains(errors, e => e.Path.EndsWith(".runCondition", StringComparison.Ordinal));
    }

    [Fact]
    public void Equal_bounds_in_one_run_condition_are_fine()
    {
        var pack = new ContentPack
        {
            Objects = [ContentJson.Parse<ObjectDefinition>(Vow("only-hard", """{ "difficultyAtLeast": "hard", "difficultyAtMost": "hard" }"""))],
        };

        Assert.Empty(ContentPublishing.Check(pack, events: true));
    }

    // ---- S5.5-01: CONTENT.md event 5 «Даже не вспотел» — the acceptance test (EV9) ----

    [Fact]
    public void Not_even_sweating_binds_the_next_run_to_hard_and_a_hard_completion_meets_it()
    {
        // Given Вася draws «Даже не вспотел» from the bad deck with his sack
        var s = Season(Pack(badEvent: "not-even-sweating"));
        var vasya = s.PlayerId("Вася");
        s.Give("Вася", "sack-bad");
        s.NextRandom(0).Used("Вася", "sack-bad");

        // Then the condition waits for his next roll
        Assert.Equal(new EventDrawn(vasya, "bad", "not-even-sweating", EventKind.Bad, EventSource.Item), Assert.Single(s.LastEvents<EventDrawn>()));
        var modified = Assert.Single(s.LastEvents<NextRollModified>());
        Assert.Equal(("not-even-sweating", AtLeast(Difficulty.Hard)), (modified.Modifier.ObjectId, modified.Modifier.RunCondition));
        var waiting = Assert.Single(s.Player("Вася").Wallet.NextRoll);
        Assert.Equal(AtLeast(Difficulty.Hard), waiting.RunCondition);

        // When he rolls: the condition is fixed in the snapshot with the policy of the rules (seen on the run's page)
        s.RollTitle("Вася", "Alan Wake");
        var offer = s.Player("Вася").Offer!;
        // The snapshot keeps the kind of its event, so a condition given back keeps its place in the order (D-586)
        Assert.Equal([new RunCondition("not-even-sweating", AtLeast(Difficulty.Hard), EventKind: EventKind.Bad)], offer.Snapshot.Conditions);
        Assert.Equal(UnmetConditionPolicy.NoDiceKeepCoins, offer.Snapshot.ConditionPolicy);
        Assert.Equal(offer.Snapshot, Assert.Single(s.LastEvents<GameRolled>()).Snapshot);
        Assert.Empty(s.Player("Вася").Wallet.NextRoll);

        // And starts: the run keeps it, the waiting lists are empty (D-405, D-507)
        s.Start("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        Assert.Equal(offer.Snapshot.Conditions, s.State.Runs[runId].Snapshot.Conditions);
        Assert.Empty(s.Player("Вася").Wallet.CurrentRoll);
        Assert.Empty(s.Player("Вася").Wallet.NextRoll);
        Assert.Null(s.State.Runs[runId].Condition);
        var coins = s.Player("Вася").Coins;

        // When he completes it on hard: two d6 show 4 and 5
        s.NextRandom(4, 5).Complete("Вася", Difficulty.Hard);

        // Then the check (met) comes after RunCompleted and before the dice, which are as usual
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunCompleted), typeof(RunConditionChecked), typeof(CompletionRolled), typeof(PointsChanged), typeof(PlayerMoved), typeof(CoinsChanged)],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(new RunConditionChecked(runId, vasya, Difficulty.Hard, Met: true), Check(s));
        Assert.Equal(new CompletionRolled(runId, vasya, [new Die(6, 4), new Die(6, 5)], []), Assert.Single(s.LastEvents<CompletionRolled>()));
        var run = s.State.Runs[runId];
        Assert.Equal(new RunConditionState(Met: true), run.Condition);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal((9, "c9", coins + 6), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    [Fact]
    public void Extreme_meets_not_lower_than_hard_too()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-hard");

        var runId = Completed(s, Difficulty.Extreme, [1, 1, 0]);

        Assert.True(s.State.Runs[runId].Condition!.Met);
        Assert.Equal(2, s.Player("Вася").Points);
    }

    // ---- S5.5-02: unmet — no dice, but coins (EV10) ----

    [Fact]
    public void Unmet_condition_gives_coins_but_no_dice()
    {
        // Given Вася must play his next game on hard; the pool: Alan Wake (6 h) and Silent Hill
        var s = Season(games: [("Alan Wake", 6), ("Silent Hill", 3)]);
        var vasya = s.PlayerId("Вася");
        Vowed(s, "Вася", "vow-hard");
        var runId = Playing(s);
        var coins = s.Player("Вася").Coins;

        // When he completes it on normal
        NoThrowExpected(s).Complete("Вася", Difficulty.Normal);

        // Then the check is unmet, nothing is thrown (the scripted 0 is still there), no points, no move, coins by 6 hours
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunCompleted), typeof(RunConditionChecked), typeof(CompletionRolled), typeof(CoinsChanged)],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(new RunConditionChecked(runId, vasya, Difficulty.Normal, Met: false), Check(s));
        Assert.Equal(new CompletionRolled(runId, vasya, [], []), Assert.Single(s.LastEvents<CompletionRolled>()));
        Assert.Equal(new CoinsChanged(vasya, 6, CoinsReason.CompletionReward, runId), Assert.Single(s.LastEvents<CoinsChanged>()));
        Assert.Equal(1, s.Random.ScriptedLeft);

        // And the run is completed, its game completed for everyone
        var run = s.State.Runs[runId];
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Empty(run.Dice);
        Assert.Empty(run.ChallengeDice);
        Assert.Equal(new RunConditionState(Met: false), run.Condition);
        Assert.Equal((0, "start", coins + 6), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));

        s.Roll("Петя");
        Assert.Equal(s.GameId("Silent Hill"), s.Player("Петя").Offer!.GameId);
    }

    // ---- S5.5-03: «без кубов» is no throw at all (contract: «+1 кубик» keeps waiting) ----

    [Fact]
    public void Without_a_throw_an_extra_die_keeps_waiting_and_neither_the_challenge_nor_the_difficulty_event_comes()
    {
        // Given Вася must play on easy at most, holds «+1 кубик» for the next throw and claims the challenge
        var s = Season(rules: WithChallenges);
        Vowed(s, "Вася", "vow-easy", "more-dice");
        Assert.Equal("more-dice", Assert.Single(s.Player("Вася").Wallet.NextDice).ObjectId);
        var runId = Playing(s);
        var coins = s.Player("Вася").Coins;

        // When he completes it on «выше сложной» with the challenge
        NoThrowExpected(s).Complete("Вася", Difficulty.Extreme, challengeDone: true);

        // Then no dice, no challenge dice, no good event of the difficulty (nothing drawn, nothing missed), coins as usual
        ScenarioAssert.Accepted(s);
        Assert.Equal(new CompletionRolled(runId, s.PlayerId("Вася"), [], []), Assert.Single(s.LastEvents<CompletionRolled>()));
        Assert.False(Check(s).Met);
        Assert.Empty(s.LastEvents<PointsChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Empty(s.LastEvents<EventDrawMissed>());
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
        Assert.False(s.State.Runs[runId].DifficultyEventDrawn);
        Assert.Equal(1, s.Random.ScriptedLeft);
        Assert.Equal(coins + 6, s.Player("Вася").Coins);

        // And «+1 кубик» waits for the next throw
        Assert.Equal("more-dice", Assert.Single(s.Player("Вася").Wallet.NextDice).ObjectId);

        // When his next run (3 hours, no condition) is completed: one die by hours and the extra one
        s.Advance(TimeSpan.FromHours(1));
        s.Roll("Вася").Start("Вася");
        var next = s.Player("Вася").ActiveRunId!.Value;
        s.NextRandom(1, 2).Complete("Вася", Difficulty.Normal);

        // The extra die of an item is kept apart from the dice by hours (D-475): one of each
        Assert.Single(s.State.Runs[next].Dice);
        Assert.Equal("more-dice", Assert.Single(s.State.Runs[next].DiceMods.Sources));
        Assert.Single(s.State.Runs[next].DiceMods.ExtraDice);
        Assert.Empty(s.Player("Вася").Wallet.NextDice);
        Assert.Empty(s.LastEvents<RunConditionChecked>());
        Assert.Null(s.State.Runs[next].Condition);
    }

    // ---- S5.5-04: lowering by the proof makes it unmet — the dice go (EV10) ----

    [Fact]
    public void Lowering_the_difficulty_on_approval_takes_the_dice_back()
    {
        // Given Вася completed Alan Wake on hard under «не ниже сложной»: 4 + 5 → 9 points, c9, 6 coins
        var s = Season();
        var vasya = s.PlayerId("Вася");
        Vowed(s, "Вася", "vow-hard");
        var runId = Completed(s, Difficulty.Hard, [4, 5]);
        var coins = s.Player("Вася").Coins;

        // When the admin approves the proof on normal
        s.Act(new ApproveProof(runId, Difficulty.Normal, "По пруфу — нормальная"));

        // Then the difficulty changes, the check is unmet, one difference takes all 9 points and cells; coins stay
        ScenarioAssert.Accepted(s);
        var changed = s.IndexOf(e => e is RunDifficultyChanged);
        var checkedAt = s.IndexOf(e => e is RunConditionChecked);
        var points = s.IndexOf(e => e is PointsChanged);
        Assert.True(changed >= 0 && changed < checkedAt && checkedAt < points, string.Join(", ", s.LastTypes()));
        Assert.Equal(new RunConditionChecked(runId, vasya, Difficulty.Normal, Met: false), Check(s));
        Assert.Equal(new PointsChanged(vasya, -9, PointsReason.RunCorrection, runId), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal(
            new PlayerMoved(vasya, "c9", "start", -9, [.. BackToStart(9)], MoveReason.RunCorrection, runId),
            Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Empty(s.LastEvents<CoinsChanged>());

        var run = s.State.Runs[runId];
        Assert.Equal(Difficulty.Normal, run.Difficulty);
        Assert.Empty(run.Dice);

        // The dice are held, recalculated to d4 by Q-5, for the condition becoming met again (D-593)
        Assert.Equal(new RunConditionState(Met: false, Held: new RunHeldDice([new Die(4, 3), new Die(4, 4)], [], null)), run.Condition);
        Assert.Equal((0, "start", coins), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    [Fact]
    public void Lowering_out_of_the_condition_by_the_admin_takes_the_dice_back_in_one_difference()
    {
        var s = Season();
        var vasya = s.PlayerId("Вася");
        Vowed(s, "Вася", "vow-hard");
        var runId = Completed(s, Difficulty.Hard, [6, 2]);

        s.Act(new ChangeRunDifficulty(runId, Difficulty.Easy, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunDifficultyChanged), typeof(RunConditionChecked), typeof(PointsChanged), typeof(PlayerMoved)],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(new RunConditionChecked(runId, vasya, Difficulty.Easy, Met: false), Check(s));
        Assert.Equal(-8, Assert.Single(s.LastEvents<PointsChanged>()).Delta);
        Assert.Equal((0, "start"), (s.Player("Вася").Points, s.Player("Вася").CellId));
        Assert.Empty(s.State.Runs[runId].Dice);
    }

    [Fact]
    public void A_change_that_keeps_the_condition_met_is_checked_again_and_corrected_as_usual()
    {
        // extreme → hard under «не ниже сложной»: both d6, the dice stay, the check is written for hard
        var s = Season();
        Vowed(s, "Вася", "vow-hard");
        var runId = Completed(s, Difficulty.Extreme, [5, 2, 0]);

        s.Act(new ChangeRunDifficulty(runId, Difficulty.Hard, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new RunConditionChecked(runId, s.PlayerId("Вася"), Difficulty.Hard, Met: true), Check(s));
        Assert.Equal([new Die(6, 5), new Die(6, 2)], s.State.Runs[runId].Dice);
        Assert.Empty(s.LastEvents<PointsChanged>());
        Assert.Equal(7, s.Player("Вася").Points);
    }

    [Fact]
    public void A_difficulty_change_of_a_run_without_conditions_writes_no_check()
    {
        var s = Season();
        var runId = Completed(s, Difficulty.Hard, [5, 2]);

        s.Act(new ChangeRunDifficulty(runId, Difficulty.Normal, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<RunConditionChecked>());
        Assert.Null(s.State.Runs[runId].Condition);
        Assert.Equal([new Die(4, 4), new Die(4, 2)], s.State.Runs[runId].Dice);
    }

    // ---- S5.5-05: raising into the condition throws the missing dice (EV10; ApproveProof cannot raise) ----

    [Fact]
    public void Raising_into_the_condition_throws_the_dice_by_the_hours_at_the_new_die()
    {
        // Given the run went without dice: «не ниже сложной», completed on normal
        var s = Season();
        var vasya = s.PlayerId("Вася");
        Vowed(s, "Вася", "vow-hard");
        var runId = Completed(s, Difficulty.Normal, []);
        var coins = s.Player("Вася").Coins;

        // When the admin sets hard by the proof: two d6 show 5 and 2
        s.NextRandom(5, 2).Act(new ChangeRunDifficulty(runId, Difficulty.Hard, "По пруфу — сложная"));

        // Then the check is met and carries the thrown dice; one difference of 7 points and cells
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunDifficultyChanged), typeof(RunConditionChecked), typeof(PointsChanged), typeof(PlayerMoved)],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(new RunConditionChecked(runId, vasya, Difficulty.Hard, Met: true, Dice: [new Die(6, 5), new Die(6, 2)]), Check(s));
        Assert.Equal(new PointsChanged(vasya, 7, PointsReason.RunCorrection, runId), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal(new PlayerMoved(vasya, "start", "c7", 7, [.. Forward(0, 7)], MoveReason.RunCorrection, runId), Assert.Single(s.LastEvents<PlayerMoved>()));
        Assert.Equal(0, s.Random.ScriptedLeft);

        var run = s.State.Runs[runId];
        Assert.Equal([new Die(6, 5), new Die(6, 2)], run.Dice);
        Assert.Equal(new RunConditionState(Met: true), run.Condition);
        Assert.Equal((7, "c7", coins), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    [Fact]
    public void Lowering_into_the_condition_on_approval_throws_the_dice_and_the_claimed_challenge_dice()
    {
        // Given «не выше лёгкой», completed on «выше сложной» with the challenge: no dice, no challenge dice, no event
        var s = Season(rules: WithChallenges);
        var vasya = s.PlayerId("Вася");
        Vowed(s, "Вася", "vow-easy");
        var runId = Completed(s, Difficulty.Extreme, [], challenge: true);
        Assert.Empty(s.State.Runs[runId].ChallengeDice);

        // When the admin approves it on easy: two d2 show 2 and 1, the challenge d2 shows 2
        s.NextRandom(2, 1, 2).Act(new ApproveProof(runId, Difficulty.Easy, "По пруфу — лёгкая"));

        // Then the check is met with both kinds of dice, 5 points and cells
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new RunConditionChecked(runId, vasya, Difficulty.Easy, Met: true, Dice: [new Die(2, 2), new Die(2, 1)], ChallengeDice: [new Die(2, 2)]),
            Check(s));
        Assert.Equal(5, Assert.Single(s.LastEvents<PointsChanged>()).Delta);
        var run = s.State.Runs[runId];
        Assert.Equal([new Die(2, 2), new Die(2, 1)], run.Dice);
        Assert.Equal([new Die(2, 2)], run.ChallengeDice);
        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Equal((5, "c5"), (s.Player("Вася").Points, s.Player("Вася").CellId));
    }

    [Fact]
    public void Raising_to_extreme_into_the_condition_draws_the_difficulty_event_once_per_run()
    {
        // Given «не ниже выше сложной», completed on hard: no dice
        var s = Season();
        Vowed(s, "Вася", "vow-extreme");
        var runId = Completed(s, Difficulty.Hard, []);

        // When the admin raises it to extreme: dice 5, 2, then the good deck's ticket
        s.NextRandom(5, 2, 0).Act(new ChangeRunDifficulty(runId, Difficulty.Extreme, "Выше сложной по пруфу"));

        ScenarioAssert.Accepted(s);
        Assert.True(Check(s).Met);
        var drawn = Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal((EventSource.Difficulty, "coins-plus-2"), (drawn.Source, drawn.ObjectId));
        Assert.True(s.State.Runs[runId].DifficultyEventDrawn);

        // When it goes down to hard (unmet, the dice go) and back up to extreme (dice again)
        s.Act(new ChangeRunDifficulty(runId, Difficulty.Hard, "Всё же сложная"));
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.State.Runs[runId].Dice);
        NoThrowExpected(s).Act(new ChangeRunDifficulty(runId, Difficulty.Extreme, "Всё же выше сложной"));

        // Then the same dice are back, not thrown again (D-593), and the run drew its event already (D-557)
        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(6, 5), new Die(6, 2)], s.State.Runs[runId].Dice);
        Assert.Equal(1, s.Random.ScriptedLeft);
        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Empty(s.LastEvents<EventDrawMissed>());
        Assert.Equal(7, s.Player("Вася").Points);
    }

    // ---- S5.5-06: an hours correction of a run without dice by its condition throws nothing ----

    [Fact]
    public void Correcting_the_hours_of_a_run_without_dice_throws_nothing_and_changes_the_coins()
    {
        var s = Season();
        var vasya = s.PlayerId("Вася");
        Vowed(s, "Вася", "vow-hard");
        var runId = Completed(s, Difficulty.Normal, []);
        var coins = s.Player("Вася").Coins;

        // When the admin corrects the hours from 6 to 12
        NoThrowExpected(s).Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));

        // Then no die is thrown or taken, no points, no move; the coins follow the hours (D-96: 12 instead of 6)
        ScenarioAssert.Accepted(s);
        Assert.Equal(1, s.Random.ScriptedLeft);
        Assert.Empty(s.State.Runs[runId].Dice);
        Assert.Empty(s.LastEvents<PointsChanged>());
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.Empty(s.LastEvents<RunConditionChecked>());
        Assert.Equal(new CoinsChanged(vasya, 6, CoinsReason.RunCorrection, runId), Assert.Single(s.LastEvents<CoinsChanged>()));
        Assert.Equal((0, "start", coins + 6), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
    }

    [Fact]
    public void After_an_hours_correction_raising_into_the_condition_throws_dice_by_the_corrected_hours()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-hard");
        var runId = Completed(s, Difficulty.Normal, []);
        s.Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));
        ScenarioAssert.Accepted(s);

        // 12 hours: four d6
        s.NextRandom(1, 2, 3, 4).Act(new ChangeRunDifficulty(runId, Difficulty.Hard, "По пруфу — сложная"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(6, 1), new Die(6, 2), new Die(6, 3), new Die(6, 4)], Check(s).Dice);
        Assert.Equal(10, s.Player("Вася").Points);
    }

    [Fact]
    public void Correcting_the_hours_of_a_met_run_adds_dice_as_usual()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-hard");
        var runId = Completed(s, Difficulty.Hard, [4, 5]);

        s.NextRandom(6, 1).Act(new CorrectRunHours(runId, 12, "Часы по HLTB"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([new Die(6, 4), new Die(6, 5), new Die(6, 6), new Die(6, 1)], s.State.Runs[runId].Dice);
        Assert.Equal(16, s.Player("Вася").Points);
    }

    // ---- S5.5-08…11: contradicting and compatible conditions (EV11, D-506) ----

    [Fact]
    public void A_contradicting_condition_waits_for_the_next_run()
    {
        // Given Вася got «не ниже сложной», then «не выше лёгкой»
        var s = Season();
        Vowed(s, "Вася", "vow-hard", "vow-easy");
        Assert.Equal(2, s.Player("Вася").Wallet.NextRoll.Count);

        // When he rolls
        s.RollTitle("Вася", "Alan Wake");

        // Then the earlier goes on this run; the contradicting one waits and is listed as waiting
        Assert.Equal([new RunCondition("vow-hard", AtLeast(Difficulty.Hard))], s.Player("Вася").Offer!.Snapshot.Conditions);
        var applied = Assert.Single(s.LastEvents<RollModifiersApplied>());
        Assert.Contains(applied.Waiting, m => m.ObjectId == "vow-easy");
        Assert.DoesNotContain(applied.Modifiers, m => m.ObjectId == "vow-easy");
        Assert.Equal(AtMost(Difficulty.Easy), Assert.Single(s.Player("Вася").Wallet.NextRoll).RunCondition);

        // And it still waits after the start and the completion on hard (met)
        s.Start("Вася");
        Assert.Equal("vow-easy", Assert.Single(s.Player("Вася").Wallet.NextRoll).ObjectId);
        s.NextRandom(4, 5).Complete("Вася", Difficulty.Hard);
        Assert.True(Check(s).Met);
        s.Advance(TimeSpan.FromHours(1));

        // When he rolls again: now it is the next run's condition
        s.Roll("Вася");
        Assert.Equal([new RunCondition("vow-easy", AtMost(Difficulty.Easy))], s.Player("Вася").Offer!.Snapshot.Conditions);
        Assert.Empty(s.Player("Вася").Wallet.NextRoll);
    }

    [Fact]
    public void The_earlier_of_two_contradicting_conditions_goes_first()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-easy", "vow-hard");

        s.RollTitle("Вася", "Alan Wake");

        Assert.Equal([new RunCondition("vow-easy", AtMost(Difficulty.Easy))], s.Player("Вася").Offer!.Snapshot.Conditions);
        Assert.Equal("vow-hard", Assert.Single(s.Player("Вася").Wallet.NextRoll).ObjectId);
    }

    [Fact]
    public void A_condition_contradicting_the_folded_ones_waits()
    {
        // «не ниже нормальной» and «не выше сложной» fold to normal…hard; «не ниже выше сложной» crosses «не выше сложной»
        var s = Season();
        Vowed(s, "Вася", "vow-normal", "vow-at-most-hard", "vow-extreme");

        s.RollTitle("Вася", "Alan Wake");

        Assert.Equal(
            [new RunCondition("vow-normal", AtLeast(Difficulty.Normal)), new RunCondition("vow-at-most-hard", AtMost(Difficulty.Hard))],
            s.Player("Вася").Offer!.Snapshot.Conditions);
        Assert.Equal("vow-extreme", Assert.Single(s.Player("Вася").Wallet.NextRoll).ObjectId);
    }

    [Fact]
    public void Equal_bounds_from_two_conditions_are_compatible()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-hard", "vow-at-most-hard");

        var runId = Completed(s, Difficulty.Hard, [4, 5]);

        Assert.Equal(2, s.State.Runs[runId].Snapshot.Conditions.Count);
        Assert.Empty(s.Player("Вася").Wallet.NextRoll);
        Assert.True(s.State.Runs[runId].Condition!.Met);
    }

    [Theory]
    [InlineData(Difficulty.Easy, false, false)]
    [InlineData(Difficulty.Normal, true, false)]
    [InlineData(Difficulty.Hard, true, false)]
    [InlineData(Difficulty.Extreme, false, false)]
    [InlineData(Difficulty.Easy, false, true)]
    [InlineData(Difficulty.Normal, true, true)]
    [InlineData(Difficulty.Hard, true, true)]
    [InlineData(Difficulty.Extreme, false, true)]
    public void Compatible_conditions_all_have_to_hold(Difficulty difficulty, bool met, bool oneCondition)
    {
        // «не ниже нормальной» and «не выше сложной», as two conditions or one with both bounds; any unmet bound fails the run
        var s = Season();
        Vowed(s, "Вася", oneCondition ? new[] { "vow-normal-to-hard" } : ["vow-normal", "vow-at-most-hard"]);
        var runId = Playing(s);
        Assert.Equal(oneCondition ? 1 : 2, s.State.Runs[runId].Snapshot.Conditions.Count);

        s.NextRandom(met ? new[] { 1, 1 } : []).Complete("Вася", difficulty);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new RunConditionChecked(runId, s.PlayerId("Вася"), difficulty, met), Check(s));
        Assert.Equal(met ? 2 : 0, s.State.Runs[runId].Dice.Count);
        Assert.Equal(met ? 2 : 0, s.Player("Вася").Points);
        Assert.Empty(s.LastEvents<EventDrawn>());
    }

    // ---- S5.5-12: a condition closes with its run ----

    [Fact]
    public void A_condition_closes_with_its_run_and_the_next_roll_has_none()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-hard");
        Completed(s, Difficulty.Normal, []);

        s.Roll("Вася");

        Assert.Empty(s.Player("Вася").Offer!.Snapshot.Conditions);
        Assert.Null(s.Player("Вася").Offer!.Snapshot.ConditionPolicy);
        Assert.Empty(s.Player("Вася").Wallet.NextRoll);
    }

    [Fact]
    public void A_roll_without_conditions_has_no_policy_in_its_snapshot()
    {
        var s = Season();

        var runId = Completed(s, Difficulty.Normal, [1, 1]);

        Assert.Empty(s.State.Runs[runId].Snapshot.Conditions);
        Assert.Null(s.State.Runs[runId].Snapshot.ConditionPolicy);
        Assert.Null(s.State.Runs[runId].Condition);
        Assert.Empty(s.LastEvents<RunConditionChecked>());
    }

    // ---- S5.5-13: «условие неприменимо» (D-546 (2)) ----

    [Fact]
    public void Waiving_an_unmet_condition_throws_the_dice_and_passes_the_condition_on()
    {
        // Given the run went without dice: «не ниже сложной» on a game without difficulties, declared normal
        var s = Season();
        var vasya = s.PlayerId("Вася");
        Vowed(s, "Вася", "vow-hard");
        var runId = Completed(s, Difficulty.Normal, []);

        // When the admin marks the condition «неприменимо»: two d4 show 3 and 1
        s.NextRandom(3, 1).Act(new WaiveRunCondition(runId, "В игре нет сложностей"));

        // Then it counts as met with the thrown dice, the difference comes, the condition goes to the next roll
        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunConditionChecked), typeof(PointsChanged), typeof(PlayerMoved), typeof(NextRollModified)],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(
            new RunConditionChecked(runId, vasya, Difficulty.Normal, Met: true, Dice: [new Die(4, 3), new Die(4, 1)], Waived: true),
            Check(s));
        Assert.Equal(new PointsChanged(vasya, 4, PointsReason.RunCorrection, runId), Assert.Single(s.LastEvents<PointsChanged>()));
        Assert.Equal(new PlayerMoved(vasya, "start", "c4", 4, [.. Forward(0, 4)], MoveReason.RunCorrection, runId), Assert.Single(s.LastEvents<PlayerMoved>()));
        var passed = Assert.Single(s.LastEvents<NextRollModified>());
        Assert.Equal((vasya, "vow-hard", AtLeast(Difficulty.Hard)), (passed.PlayerId, passed.Modifier.ObjectId, passed.Modifier.RunCondition));

        var run = s.State.Runs[runId];
        Assert.Equal(new RunConditionState(Met: true, Waived: true), run.Condition);
        Assert.Equal([new Die(4, 3), new Die(4, 1)], run.Dice);
        Assert.Equal(AtLeast(Difficulty.Hard), Assert.Single(s.Player("Вася").Wallet.NextRoll).RunCondition);

        // And the next roll fixes it
        s.Roll("Вася");
        Assert.Equal([new RunCondition("vow-hard", AtLeast(Difficulty.Hard))], s.Player("Вася").Offer!.Snapshot.Conditions);
    }

    [Fact]
    public void Waiving_a_met_condition_throws_nothing_and_still_passes_it_on()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-hard");
        var runId = Completed(s, Difficulty.Hard, [4, 5]);

        NoThrowExpected(s).Act(new WaiveRunCondition(runId, "Сложность в игре не выбирается"));

        ScenarioAssert.Accepted(s);
        Assert.Equal([typeof(RunConditionChecked), typeof(NextRollModified)], s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(new RunConditionChecked(runId, s.PlayerId("Вася"), Difficulty.Hard, Met: true, Waived: true), Check(s));
        Assert.Equal(1, s.Random.ScriptedLeft);
        Assert.Equal([new Die(6, 4), new Die(6, 5)], s.State.Runs[runId].Dice);
        Assert.Equal(9, s.Player("Вася").Points);
    }

    [Fact]
    public void Waiving_passes_every_condition_of_the_snapshot_on()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-normal", "vow-at-most-hard");
        var runId = Completed(s, Difficulty.Extreme, []);

        s.NextRandom(2, 2).Act(new WaiveRunCondition(runId, "Неприменимо"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(["vow-normal", "vow-at-most-hard"], s.LastEvents<NextRollModified>().Select(m => m.Modifier.ObjectId));
        Assert.Equal([new Die(6, 2), new Die(6, 2)], s.State.Runs[runId].Dice);
    }

    [Fact]
    public void A_waived_condition_is_not_checked_again_when_the_difficulty_changes()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-hard");
        var runId = Completed(s, Difficulty.Normal, []);
        s.NextRandom(3, 1).Act(new WaiveRunCondition(runId, "Неприменимо"));
        ScenarioAssert.Accepted(s);

        // normal → easy: 3 → ⌈3 × 2 / 4⌉ = 2, 1 → 1 (Q-5), as without a condition
        s.Act(new ChangeRunDifficulty(runId, Difficulty.Easy, Comment));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<RunConditionChecked>());
        Assert.Equal([new Die(2, 2), new Die(2, 1)], s.State.Runs[runId].Dice);
        Assert.Equal(new RunConditionState(Met: true, Waived: true), s.State.Runs[runId].Condition);
        Assert.Equal(3, s.Player("Вася").Points);
    }

    [Fact]
    public void Waiving_is_refused_without_a_comment_twice_without_a_condition_or_before_the_completion()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-hard");
        var playing = Playing(s);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new WaiveRunCondition(playing, "Неприменимо")), RejectionCodes.RunNotCompleted);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new WaiveRunCondition(Guid.Parse("7e570000-0000-0000-0000-000000000001"), "Неприменимо")), RejectionCodes.RunUnknown);

        s.Complete("Вася", Difficulty.Normal);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new WaiveRunCondition(playing, " ")), RejectionCodes.CommentRequired);
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new WaiveRunCondition(playing, new string('я', Limits.MaxCommentLength + 1))), RejectionCodes.CommentTooLong);

        s.Act(new WaiveRunCondition(playing, "Неприменимо"));
        ScenarioAssert.Accepted(s);
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new WaiveRunCondition(playing, "Ещё раз")), RejectionCodes.RunNoCondition);

        // Петя's run had no condition
        s.Roll("Петя").Start("Петя").NextRandom(1).Complete("Петя");
        var petyas = s.State.Runs.Values.Single(r => r.PlayerId == s.PlayerId("Петя")).RunId;
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new WaiveRunCondition(petyas, "Неприменимо")), RejectionCodes.RunNoCondition);
    }

    // ---- S5.5-14: a condition drawn onto a playing player waits for his next roll (EV9, EV17) ----

    [Fact]
    public void A_condition_drawn_onto_a_playing_player_waits_for_his_next_roll()
    {
        // Given Петя plays Silent Hill; Вася puts «Сглаз» on him and the bad deck gives «Даже не вспотел»
        var s = Season(Pack(badEvent: "not-even-sweating"));
        var petyasRun = Playing(s, "Silent Hill", "Петя");
        s.Give("Вася", "jinx");
        s.NextRandom(0).Used("Вася", "jinx", target: "Петя");

        var drawn = Assert.Single(s.LastEvents<EventDrawn>());
        Assert.Equal((s.PlayerId("Петя"), "not-even-sweating", EventSource.Incoming), (drawn.PlayerId, drawn.ObjectId, drawn.Source));
        Assert.Equal(AtLeast(Difficulty.Hard), Assert.Single(s.Player("Петя").Wallet.NextRoll).RunCondition);
        Assert.Empty(s.State.Runs[petyasRun].Snapshot.Conditions);

        // When Петя completes the current run on normal: no condition, the die as usual
        s.NextRandom(2).Complete("Петя", Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<RunConditionChecked>());
        Assert.Null(s.State.Runs[petyasRun].Condition);
        Assert.Equal(2, s.Player("Петя").Points);

        // Then his next roll carries it
        s.Advance(TimeSpan.FromHours(1));
        s.Roll("Петя");
        var condition = Assert.Single(s.Player("Петя").Offer!.Snapshot.Conditions);
        Assert.Equal(("not-even-sweating", AtLeast(Difficulty.Hard)), (condition.ObjectId, condition.Condition));
    }

    // ---- S5.5-15: drop, tech reroll, reject (Н-7) ----

    [Fact]
    public void A_drop_closes_the_condition_with_the_run()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-hard");
        var runId = s.PlayingFor("Вася", "Alan Wake");
        Assert.Single(s.State.Runs[runId].Snapshot.Conditions);

        s.Dropped("Вася", 1, 1, 0);

        Assert.DoesNotContain(s.Player("Вася").Wallet.NextRoll, m => m.RunCondition is not null);
        Assert.Empty(s.Player("Вася").Wallet.CurrentRoll);
        Assert.Empty(s.LastEvents<RunConditionChecked>());
        s.Roll("Вася");
        Assert.Empty(s.Player("Вася").Offer!.Snapshot.Conditions);
    }

    [Theory]
    [InlineData(TechRerollReason.DoesNotLaunch)]
    [InlineData(TechRerollReason.Wish)]
    public void A_tech_reroll_puts_the_conditions_back_first_and_the_new_roll_fixes_them(TechRerollReason reason)
    {
        // Given Вася plays under «не ниже сложной» while «не выше лёгкой» waits (contradicting)
        var s = Season(rules: r => r with { Roll = r.Roll with { WishRerollTags = ["Horror"] } });
        var vasya = s.PlayerId("Вася");
        Vowed(s, "Вася", "vow-hard", "vow-easy");
        var first = Playing(s);

        // When he tech-rerolls: the game is not his fault (Н-7)
        s.Act(new TechReroll(vasya, reason, null));

        // Then the new roll takes the condition of the run again, before the one that waited
        ScenarioAssert.Accepted(s);
        var rolled = Assert.Single(s.LastEvents<GameRolled>());
        Assert.Equal([new RunCondition("vow-hard", AtLeast(Difficulty.Hard))], rolled.Snapshot.Conditions);
        Assert.Equal("vow-easy", Assert.Single(s.Player("Вася").Wallet.NextRoll).ObjectId);
        Assert.Empty(s.LastEvents<RunConditionChecked>());
        Assert.Null(s.State.Runs[first].Condition);

        // And the new run is checked by it
        s.Start("Вася");
        var second = s.Player("Вася").ActiveRunId!.Value;
        s.Complete("Вася", Difficulty.Normal);
        Assert.Equal(new RunConditionChecked(second, vasya, Difficulty.Normal, Met: false), Check(s));
    }

    [Fact]
    public void A_reject_of_a_run_without_dice_by_its_condition_takes_back_only_the_coins()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-hard");
        var coins = s.Player("Вася").Coins;
        var runId = Completed(s, Difficulty.Normal, []);

        s.Act(new RejectProof(runId, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(RunStatus.Rejected, s.State.Runs[runId].Status);
        Assert.Equal(0, s.LastEvents<PointsChanged>().Sum(p => p.Delta));
        Assert.Equal((0, "start", coins), (s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Coins));
        Assert.DoesNotContain(s.Player("Вася").Wallet.NextRoll, m => m.RunCondition is not null);
    }

    // ---- S5.5-16: countAsDrop is not supported yet ----

    [Fact]
    public void Count_as_drop_is_reported_unsupported_and_a_ruleset_with_it_is_refused()
    {
        var rules = TestRuleset.Create().WithEvents();

        Assert.Contains(RulesetSupport.Unsupported(Policy(rules, UnmetConditionPolicy.CountAsDrop)), e => e.Path == "reward.unmetConditionPolicy");
        Assert.DoesNotContain(RulesetSupport.Unsupported(Policy(rules, UnmetConditionPolicy.Ignore)), e => e.Path == "reward.unmetConditionPolicy");
        Assert.DoesNotContain(RulesetSupport.Unsupported(Policy(rules, UnmetConditionPolicy.NoDiceKeepCoins)), e => e.Path == "reward.unmetConditionPolicy");

        var s = Season();
        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ChangeRuleset(Policy(x.Ruleset, UnmetConditionPolicy.CountAsDrop))), RejectionCodes.RulesetInvalid);
    }

    // ---- S5.5-17: the policy is the snapshot's (D-546 (2)) ----

    [Fact]
    public void The_policy_of_the_roll_holds_when_the_rules_change_to_ignore()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-hard");
        var runId = Playing(s);
        s.WithRuleset(r => Policy(r, UnmetConditionPolicy.Ignore));

        NoThrowExpected(s).Complete("Вася", Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.Equal(UnmetConditionPolicy.NoDiceKeepCoins, s.State.Runs[runId].Snapshot.ConditionPolicy);
        Assert.Empty(s.State.Runs[runId].Dice);
        Assert.Equal(1, s.Random.ScriptedLeft);
        Assert.Equal(0, s.Player("Вася").Points);
    }

    [Fact]
    public void Under_the_ignore_policy_an_unmet_condition_is_checked_and_rewarded_as_without_it()
    {
        var s = Season(rules: r => Policy(r, UnmetConditionPolicy.Ignore));
        var vasya = s.PlayerId("Вася");
        Vowed(s, "Вася", "vow-hard");
        var runId = Playing(s);
        Assert.Equal(UnmetConditionPolicy.Ignore, s.State.Runs[runId].Snapshot.ConditionPolicy);

        s.NextRandom(3, 1).Complete("Вася", Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunCompleted), typeof(RunConditionChecked), typeof(CompletionRolled), typeof(PointsChanged), typeof(PlayerMoved), typeof(CoinsChanged)],
            s.Last.Events.Select(e => e.GetType()));
        Assert.Equal(new RunConditionChecked(runId, vasya, Difficulty.Normal, Met: false), Check(s));
        Assert.Equal([new Die(4, 3), new Die(4, 1)], s.State.Runs[runId].Dice);
        Assert.Equal(new RunConditionState(Met: false), s.State.Runs[runId].Condition);
        Assert.Equal(4, s.Player("Вася").Points);
    }

    [Fact]
    public void A_roll_under_ignore_keeps_ignore_when_the_rules_change_back()
    {
        var s = Season(rules: r => Policy(r, UnmetConditionPolicy.Ignore));
        Vowed(s, "Вася", "vow-hard");
        var runId = Playing(s);
        s.WithRuleset(r => Policy(r, UnmetConditionPolicy.NoDiceKeepCoins));

        s.NextRandom(3, 1).Complete("Вася", Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.False(Check(s).Met);
        Assert.Equal([new Die(4, 3), new Die(4, 1)], s.State.Runs[runId].Dice);
    }

    [Fact]
    public void Under_ignore_lowering_out_of_the_condition_recalculates_the_dice_as_usual()
    {
        var s = Season(rules: r => Policy(r, UnmetConditionPolicy.Ignore));
        Vowed(s, "Вася", "vow-hard");
        var runId = Completed(s, Difficulty.Hard, [4, 5]);

        s.Act(new ChangeRunDifficulty(runId, Difficulty.Normal, Comment));

        // 4 → 3, 5 → 4 (Q-5): one difference of −2; the check says unmet, the policy keeps the dice
        ScenarioAssert.Accepted(s);
        Assert.Equal(new RunConditionChecked(runId, s.PlayerId("Вася"), Difficulty.Normal, Met: false), Check(s));
        Assert.Equal([new Die(4, 3), new Die(4, 4)], s.State.Runs[runId].Dice);
        Assert.Equal(-2, Assert.Single(s.LastEvents<PointsChanged>()).Delta);
    }

    // ---- S5.5-18: undo of a completion with an unmet condition (EV38) ----

    [Fact]
    public void Undoing_a_completion_with_an_unmet_condition_restores_the_run_and_the_coins()
    {
        var s = Season();
        Vowed(s, "Вася", "vow-hard");
        var runId = Playing(s);
        var run = s.State.Runs[runId];
        var player = s.Player("Вася");
        s.Complete("Вася", Difficulty.Normal);
        ScenarioAssert.Accepted(s);

        s.Act(new UndoCommand(s.LastCommandId, "Ошибся сложностью"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(run, s.State.Runs[runId]);
        Assert.Equal(RunStatus.Playing, s.State.Runs[runId].Status);
        Assert.Equal([new RunCondition("vow-hard", AtLeast(Difficulty.Hard))], s.State.Runs[runId].Snapshot.Conditions);
        Assert.Null(s.State.Runs[runId].Condition);
        Assert.Equal((player.Coins, player.Points, player.CellId, player.Phase), (s.Player("Вася").Coins, s.Player("Вася").Points, s.Player("Вася").CellId, s.Player("Вася").Phase));

        // Completed again on hard, the condition is met
        s.NextRandom(4, 5).Complete("Вася", Difficulty.Hard);
        Assert.True(Check(s).Met);
        Assert.Equal(9, s.Player("Вася").Points);
    }

    // ---- The log ----

    [Fact]
    public void The_check_is_an_event_of_its_own_and_a_replay_gives_the_same_state()
    {
        var s = Season(rules: WithChallenges);
        Vowed(s, "Вася", "vow-hard");
        var unmet = Completed(s, Difficulty.Normal, []);
        s.NextRandom(2, 2).Act(new ChangeRunDifficulty(unmet, Difficulty.Hard, Comment));
        Vowed(s, "Вася", "vow-easy");
        var waived = Completed(s, Difficulty.Hard, [], title: null, challenge: true);
        s.NextRandom(1, 2).Act(new WaiveRunCondition(waived, "Неприменимо"));
        ScenarioAssert.Accepted(s);

        Assert.Equal("run-condition-checked", EventCatalog.Describe(typeof(RunConditionChecked)).Name);
        var check = Check(s);
        Assert.Equal(check, EventCodec.Decode(EventCodec.Encode(check)));
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log.Select(e => EventCodec.Decode(EventCodec.Encode(e)))));
    }
}
