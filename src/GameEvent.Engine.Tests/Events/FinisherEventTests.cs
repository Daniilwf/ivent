using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// Events and finishers (SPEC «Прогресс и победа», «Взаимодействие игроков»; D-510; EV16, EV42 for events): the first
/// finisher, once frozen, draws nothing — his drop and completions in free mode give no events — and nobody draws an event
/// onto him. While his finish is provisional he no longer affects others, but his own points and coins count as usual
/// (Н-2: he draws for himself). A later finisher draws as usual, but only the points and coins of the event reach him, his
/// position stays on the finish; another player cannot draw an event onto him (D-424: a draw is not points or coins).
/// Effects from others go to the target's future steps only (EV17, D-416). A random target of an event is an active
/// player (EV45). Linear map of four steps: from the start 3 + 1 (two d4 of a 6-hour game) reach the finish.
/// </summary>
public class FinisherEventTests
{
    private static readonly string[] s_titles =
    [
        "Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Amnesia", "Soma", "Prey", "Doom", "Quake", "Portal", "Limbo", "Inside",
    ];

    private static Scenario Season(ContentPack pack, Func<Ruleset, Ruleset>? rules = null, int seed = 42, params string[] players)
    {
        var ruleset = TestRuleset.Create().WithEvents();
        var s = Scenario.New(rules is null ? ruleset : rules(ruleset), seed).WithMapLength(4).WithCategory("Horror");
        foreach (var title in s_titles)
        {
            s.WithGame(title, 6, "Horror");
        }

        s.WithPlayers(players.Length > 0 ? players : ["Вася", "Петя", "Маша"]);
        return s.WithContent(pack);
    }

    /// <summary>The player rolls, starts and completes with <paramref name="dice"/>; returns the run.</summary>
    private static Guid Complete(Scenario s, string player, int[] dice, Difficulty difficulty = Difficulty.Normal)
    {
        s.Roll(player).Start(player);
        var runId = s.Player(player).ActiveRunId!.Value;
        s.NextRandom(dice).Complete(player, difficulty);
        ScenarioAssert.Accepted(s);
        s.Advance(TimeSpan.FromHours(1));
        return runId;
    }

    private static Guid Finished(Scenario s, string player) => Complete(s, player, [3, 1]);

    private static void FrozenFirst(Scenario s, string player)
    {
        s.Approve(Finished(s, player));
        Assert.True(s.Player(player).Finish?.Frozen, $"{player} is not frozen.");
    }

    private static void NoDrawIn(Scenario s)
    {
        Assert.Empty(s.LastEvents<EventDrawn>());
        Assert.Empty(s.LastEvents<EventDrawMissed>());
        Assert.Empty(s.LastEvents<ManualEffectCreated>());
    }

    private static ContentPack Decks(params string[] extra) =>
        EventSetup.Pack(
            ["coins-plus-2", "coins-minus-3", "hop-and-coins", "gamble", "jinx", "sack-risky", "sack-good", .. extra],
            EventSetup.Deck(EventKind.Good, "coins-plus-2"),
            EventSetup.Deck(EventKind.Bad, "coins-minus-3"),
            EventSetup.Deck(EventKind.Risky, "gamble"));

    // ---- The frozen first draws nothing (EV16, D-510) ----

    [Fact]
    public void The_frozen_first_drops_in_free_mode_without_an_event()
    {
        var s = Season(Decks());
        FrozenFirst(s, "Вася");
        s.Roll("Вася").Start("Вася");
        var before = s.Player("Вася");

        s.NextRandom(2, 3, 0).Act(new DropRun(s.PlayerId("Вася")));

        ScenarioAssert.Accepted(s);
        NoDrawIn(s);
        Assert.Empty(s.State.ManualEffects);
        Assert.Equal((before.Points, before.Coins), (s.Player("Вася").Points, s.Player("Вася").Coins));
    }

    [Fact]
    public void The_frozen_first_completes_above_hard_without_an_event()
    {
        var s = Season(Decks());
        FrozenFirst(s, "Вася");
        var before = s.Player("Вася");

        Complete(s, "Вася", [2, 2], Difficulty.Extreme);

        NoDrawIn(s);
        Assert.Empty(s.Player("Вася").Wallet.NextDice);
        Assert.Equal((before.Points, before.Coins), (s.Player("Вася").Points, s.Player("Вася").Coins));
    }

    [Fact]
    public void Nobody_draws_an_event_onto_the_frozen_first()
    {
        var s = Season(Decks());
        FrozenFirst(s, "Вася");
        var jinx = s.Give("Петя", "jinx");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Петя", jinx, "Вася"), RejectionCodes.ItemInvalidTarget);
        Assert.Contains("jinx", s.Inventory("Петя"));
    }

    // ---- The provisional first (Н-2) ----

    [Fact]
    public void The_provisional_first_no_longer_draws_events_onto_others()
    {
        // SPEC «Прогресс и победа»: «В это время игрок уже не влияет на других»
        var s = Season(Decks());
        Finished(s, "Вася");
        Assert.False(s.Player("Вася").Finish!.Frozen);
        var jinx = s.Give("Вася", "jinx");
        var before = s.State;
        var log = s.Log.Count;

        s.Use("Вася", jinx, "Петя");

        Assert.False(s.Last.IsAccepted, "The provisional first drew an event onto another player.");
        Assert.Contains(s.Last.Rejection!.Code, new[] { RejectionCodes.ItemInvalidTarget, RejectionCodes.PlayerFinished, RejectionCodes.InventoryFrozen });
        Assert.Equal(before, s.State);
        Assert.Equal(log, s.Log.Count);
    }

    [Fact]
    public void The_provisional_first_still_draws_for_himself_until_the_approval()
    {
        var s = Season(Decks());
        var finishing = Finished(s, "Вася");

        Complete(s, "Вася", [2, 2], Difficulty.Extreme);

        Assert.Equal(EventSource.Difficulty, Assert.Single(s.LastEvents<EventDrawn>()).Source);
        Assert.Contains(s.LastEvents<CoinsChanged>(), c => c.Delta == 2 && c.Reason != CoinsReason.CompletionReward);

        // Approved: frozen, and the next completion above hard draws nothing
        s.Approve(finishing);
        Assert.True(s.Player("Вася").Finish!.Frozen);
        Complete(s, "Вася", [2, 2], Difficulty.Extreme);
        NoDrawIn(s);
    }

    [Theory]
    [InlineData("toll")]
    [InlineData("pickpocket")]
    public void The_provisional_first_draws_an_event_of_his_own_run_but_it_reaches_nobody_else(string id)
    {
        // SPEC «Прогресс и победа»: «В это время игрок уже не влияет на других» — an event of his own completion that aims at
        // the leader or a random active player finds nobody (D-557); the frozen-first rule of targets is not enough
        var s = Season(EventSetup.Pack([id], EventSetup.Deck(EventKind.Good, id)));
        Finished(s, "Вася");
        Assert.False(s.Player("Вася").Finish!.Frozen);
        s.WithCoins("Петя", 10).WithCoins("Маша", 10).WithPoints("Петя", 3);
        var others = s.State.Players.Values.Where(p => p.PlayerId != s.PlayerId("Вася")).Select(p => (p.PlayerId, p.Points, p.Coins)).ToList();

        Complete(s, "Вася", [2, 2], Difficulty.Extreme);

        Assert.Equal((EventSource.Difficulty, id), (Assert.Single(s.LastEvents<EventDrawn>()).Source, s.LastEvents<EventDrawn>().Single().ObjectId));
        Assert.Equal(others, s.State.Players.Values.Where(p => p.PlayerId != s.PlayerId("Вася")).Select(p => (p.PlayerId, p.Points, p.Coins)));
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), c => c.PlayerId != s.PlayerId("Вася"));
    }

    [Fact]
    public void The_frozen_first_draws_nothing_when_his_run_is_rejected_with_the_drop_penalty()
    {
        var s = Season(Decks());
        FrozenFirst(s, "Вася");
        var runId = Complete(s, "Вася", [2, 2]);

        s.NextRandom(2, 3, 0).Act(new Engine.Proofs.RejectProofWithDropPenalty(runId, "Обман"));

        ScenarioAssert.Accepted(s);
        NoDrawIn(s);
        Assert.Empty(s.State.ManualEffects);
    }

    // ---- A later finisher: points and coins only (EV16, D-510, D-424) ----

    [Fact]
    public void A_later_finisher_gets_the_coins_of_an_event_but_not_its_move()
    {
        var s = Season(EventSetup.Pack(["hop-and-coins"], EventSetup.Deck(EventKind.Good, "hop-and-coins")));
        FrozenFirst(s, "Вася");
        Finished(s, "Маша");
        Assert.NotNull(s.Player("Маша").Finish);
        var cell = s.Player("Маша").CellId;

        Complete(s, "Маша", [2, 2], Difficulty.Extreme);

        Assert.Equal(EventSource.Difficulty, Assert.Single(s.LastEvents<EventDrawn>()).Source);
        Assert.Equal(3, s.LastEvents<CoinsChanged>().Where(c => c.Reason != CoinsReason.CompletionReward).Sum(c => c.Delta));
        Assert.DoesNotContain(s.LastEvents<PlayerMoved>(), m => m.PlayerId == s.PlayerId("Маша"));
        Assert.Equal(cell, s.Player("Маша").CellId);
    }

    [Fact]
    public void An_event_that_only_moves_a_later_finisher_does_nothing_but_the_item_is_spent()
    {
        // «Рискнёшь?» only moves; the item draws, it does not «only move its user» (D-422 (4) does not apply)
        var s = Season(Decks());
        FrozenFirst(s, "Вася");
        Finished(s, "Маша");
        var before = s.Player("Маша");
        s.Give("Маша", "sack-risky");

        s.NextRandom(0, 5).Used("Маша", "sack-risky");

        Assert.Equal(("risky", "gamble"), (Assert.Single(s.LastEvents<EventDrawn>()).Deck, s.LastEvents<EventDrawn>().Single().ObjectId));
        Assert.Empty(s.LastEvents<PlayerMoved>());
        Assert.DoesNotContain("sack-risky", s.Inventory("Маша"));
        Assert.Equal((before.Points, before.Coins, before.CellId), (s.Player("Маша").Points, s.Player("Маша").Coins, s.Player("Маша").CellId));
    }

    [Fact]
    public void Nobody_draws_an_event_onto_a_later_finisher()
    {
        // D-510 → D-424: a draw is more than points and coins, so the later finisher is no target of it
        var s = Season(Decks());
        FrozenFirst(s, "Вася");
        Finished(s, "Маша");
        var jinx = s.Give("Петя", "jinx");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Use("Петя", jinx, "Маша"), RejectionCodes.ItemInvalidTarget);
    }

    // ---- Effects from others: future steps only (EV17, D-416) ----

    [Fact]
    public void An_event_from_another_waits_for_the_next_step()
    {
        // Петя waits to choose among three games; Вася's «Сглаз» draws «Хоррор-неделя» onto him
        var s = Season(
            EventSetup.Pack(["jinx", "horror-week", "forced-genre"], EventSetup.Deck(EventKind.Bad, "horror-week")),
            r => r with { Roll = r.Roll with { ChoiceCount = 3 } });
        s.Roll("Петя");
        var choice = s.Player("Петя").Choice!;
        s.Give("Вася", "jinx");

        s.NextRandom(0).Used("Вася", "jinx", target: "Петя");

        Assert.Equal(EventSource.Incoming, Assert.Single(s.LastEvents<EventDrawn>()).Source);
        Assert.Equal(choice, s.Player("Петя").Choice);

        // The choice goes as offered: the forced genre waits for Петя's next roll
        s.Act(new Engine.Turns.MakeChoice(s.PlayerId("Петя"), choice.ChoiceId, choice.Options[1].Id));
        ScenarioAssert.Accepted(s);
        var run = s.State.Runs[s.Player("Петя").ActiveRunId!.Value];
        Assert.Equal(choice.Options[1].Game!.GameId, run.GameId);
        Assert.Contains("forced-genre", s.Inventory("Петя"));
    }

    [Fact]
    public void A_dice_change_from_another_players_event_goes_to_the_throw_not_yet_made()
    {
        // «Тяжёлая рука» (−1d6 to the next throw) drawn onto Петя while he plays: the throw of this run is not made yet
        var s = Season(EventSetup.Pack(["jinx", "minus-die"], EventSetup.Deck(EventKind.Bad, "minus-die")));
        s.Roll("Петя").Start("Петя");
        var first = s.Player("Петя").ActiveRunId!.Value;
        s.Give("Вася", "jinx");
        s.Used("Вася", "jinx", target: "Петя");
        Assert.Single(s.Player("Петя").Wallet.NextDice);

        s.NextRandom(1, 1, 1).Complete("Петя");

        Assert.Contains("minus-die", s.State.Runs[first].DiceMods.Sources);
        Assert.Empty(s.Player("Петя").Wallet.NextDice);

        s.Advance(TimeSpan.FromHours(1));
        var second = Complete(s, "Петя", [1, 1]);
        Assert.DoesNotContain("minus-die", s.State.Runs[second].DiceMods.Sources);
    }

    // ---- A random target of an event is an active player (EV45) ----

    [Fact]
    public void Random_target_of_an_event_is_active()
    {
        // Маша is the frozen first, Коля is inactive: «Карманник» (−2 coins, randomActive) never hits them
        var hit = new HashSet<string>();
        foreach (var seed in Enumerable.Range(1, 30))
        {
            var s = Season(
                EventSetup.Pack(["pickpocket", "sack-good"], EventSetup.Deck(EventKind.Good, "pickpocket")),
                seed: seed,
                players: ["Вася", "Петя", "Маша", "Коля"]);
            FrozenFirst(s, "Маша");
            s.Act(new SetPlayerInactive(s.PlayerId("Коля"), true));
            ScenarioAssert.Accepted(s);
            s.Give("Вася", "sack-good");

            s.Used("Вася", "sack-good");

            var robbed = Assert.Single(s.LastEvents<CoinsChanged>(), c => c.Delta == -2);
            var name = s.PlayerName(robbed.PlayerId);
            Assert.Contains(name, new[] { "Вася", "Петя" });
            hit.Add(name);
        }

        Assert.Equal(2, hit.Count);
    }
}
