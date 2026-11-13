using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// Task 5.5 after the review of `rules-auditor` and `code-reviewer` (D-593, D-591, D-592): the dice an unmet condition takes
/// come back as they were, a condition outlives a roll that ended without a game, the paid reroll keeps the order of the
/// waiting conditions, a run without a throw is out of the «after the throw» window and trigger, with events switched off a
/// condition waits, and <c>difficultyAtMost</c> works in a trigger's condition. Same season as <see cref="RunConditionTests"/>.
/// </summary>
public class RunConditionReviewTests
{
    /// <summary>An item for the window «after the throw»: +1 to the throw just made.</summary>
    private const string Polish = """
        {
          "id": "polish", "kind": "item", "name": "Полировка", "description": "К только что сделанному броску +1.",
          "rarity": "common", "price": 10, "window": "afterDice", "hostile": false, "stackable": true,
          "effect": { "target": { "selector": "self" }, "actions": [{ "type": "modifyDice", "when": "current", "stage": "add", "value": 1 }] }
        }
        """;

    /// <summary>An effect given for the season: after every throw of a run on easy at most, one coin.</summary>
    private const string EasyTip = """
        {
          "id": "easy-tip", "kind": "effect", "name": "Чаевые за лёгкую", "description": "После броска за прохождение не выше лёгкой — монетка.",
          "rarity": "common", "price": null, "hostile": false,
          "effect": { "trigger": "afterDice", "condition": { "difficultyAtMost": "easy" }, "actions": [{ "type": "changeResource", "resource": "coins", "amount": 1 }] }
        }
        """;

    private static ContentPack PackWith(string badEvent = "coins-minus-3") =>
        RunConditionTests.Pack(badEvent) is var pack
            ? pack with { Objects = [.. pack.Objects, ContentJson.Parse<ObjectDefinition>(Polish), ContentJson.Parse<ObjectDefinition>(EasyTip)] }
            : pack;

    private static Scenario Vowed(Scenario s, string player, params string[] ids)
    {
        foreach (var id in ids)
        {
            s.Give(player, id);
            s.Used(player, id);
        }

        return s;
    }

    private static Guid Play(Scenario s, string title = "Alan Wake")
    {
        s.RollTitle("Вася", title).Start("Вася");
        return s.Player("Вася").ActiveRunId!.Value;
    }

    [Fact]
    public void Dice_an_unmet_condition_took_come_back_as_they_were_with_the_dice_of_the_item()
    {
        // Given «не ниже сложной» and «+1 кубик», Alan Wake completed on hard: dice 4, 5 and the item's 3 — 12 points
        var s = RunConditionTests.Season(pack: PackWith());
        Vowed(s, "Вася", "vow-hard", "more-dice");
        var runId = Play(s);
        s.NextRandom(4, 5, 3).Complete("Вася", Difficulty.Hard);
        ScenarioAssert.Accepted(s);
        var run = s.State.Runs[runId];
        Assert.Equal(12, s.Player("Вася").Points);

        // When the admin approves on normal (unmet: all taken) and then raises it back to hard
        s.Act(new ApproveProof(runId, Difficulty.Normal, "Нормальная"));
        Assert.Equal(0, s.Player("Вася").Points);
        s.NextRandom(0).Act(new ChangeRunDifficulty(runId, Difficulty.Hard, "Всё же сложная"));

        // Then nothing is thrown again: the dice come back by Q-5 (4, 5 on d6 → 3, 4 on d4 → 5, 6 on d6) with the item's die
        ScenarioAssert.Accepted(s);
        Assert.Equal(1, s.Random.ScriptedLeft);
        var back = s.State.Runs[runId];
        Assert.Equal([new Die(6, 5), new Die(6, 6)], back.Dice);
        Assert.Equal(run.Mods, back.Mods);
        Assert.Null(back.Condition!.Held);
        Assert.Equal(CompletionRoll.Total(back), s.Player("Вася").Points);
        Assert.Equal(new RunConditionChecked(runId, s.PlayerId("Вася"), Difficulty.Hard, true, [new Die(6, 5), new Die(6, 6)]), Assert.Single(s.LastEvents<RunConditionChecked>()));
    }

    [Fact]
    public void A_condition_outlives_a_roll_whose_offer_the_admin_discarded()
    {
        // Given Вася rolled with «не ниже сложной» and the admin discarded the offer before any game started
        var s = RunConditionTests.Season(pack: PackWith());
        Vowed(s, "Вася", "vow-hard");
        s.Roll("Вася");
        Assert.Single(s.Player("Вася").Offer!.Snapshot.Conditions);
        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Игрок пропал", DiscardOffer: true));
        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<OfferDiscarded>());

        // When he rolls again: the condition belongs to his next run, not to the roll that ended
        s.Roll("Вася");

        Assert.Equal("vow-hard", Assert.Single(s.Player("Вася").Offer!.Snapshot.Conditions).ObjectId);
    }

    [Fact]
    public void A_condition_postponed_by_the_paid_reroll_waits_behind_the_older_waiting_ones()
    {
        // Given the bad deck holds «Даже не вспотел» (not lower than hard), the second reroll of a roll costs a bad event,
        // Вася rolls under «не выше лёгкой» and, while rolling, gets «не ниже выше сложной» for the next roll
        var s = RunConditionTests.Season(
            pack: PackWith("not-even-sweating"),
            rules: r => r with { Roll = r.Roll with { FreeRerollsPerRoll = 1, RerollCost = new RerollCost { Kind = RerollCostKind.BadEvent } } });
        Vowed(s, "Вася", "vow-easy");
        s.Roll("Вася");
        Vowed(s, "Вася", "vow-extreme");
        s.Act(new Reroll(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);

        // When the paid reroll draws «Даже не вспотел»: it contradicts «не выше лёгкой» of this roll
        s.Act(new Reroll(s.PlayerId("Вася")));

        // Then the event's condition is the more important one (events before items, D-505, D-586): the roll takes «Даже не
        // вспотел», and the item's «не выше лёгкой» of this roll waits ahead of the older «не ниже выше сложной»
        ScenarioAssert.Accepted(s);
        Assert.Equal(RerollPayment.BadEvent, Assert.Single(s.LastEvents<GameRerolled>()).Payment);
        Assert.Equal("not-even-sweating", Assert.Single(s.Player("Вася").Offer!.Snapshot.Conditions).ObjectId);
        Assert.Equal(["vow-easy", "vow-extreme"], s.Player("Вася").Wallet.NextRoll.Select(m => m.ObjectId));
        Assert.Equal(["vow-easy", "vow-extreme"], Assert.Single(s.LastEvents<RollModifiersApplied>()).Waiting.Select(m => m.ObjectId));
    }

    [Fact]
    public void A_run_without_a_throw_is_out_of_the_after_the_throw_window()
    {
        var s = RunConditionTests.Season(pack: PackWith());
        Vowed(s, "Вася", "vow-hard");
        Play(s);
        s.Complete("Вася", Difficulty.Normal);
        s.Give("Вася", "polish");

        var polish = s.Player("Вася").Wallet.Inventory.Single(o => o.ObjectId == "polish").InstanceId;
        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new UseItem(x.PlayerId("Вася"), polish, null)), RejectionCodes.ItemWrongWindow);
    }

    [Fact]
    public void A_completion_without_a_throw_fires_no_after_the_throw_effect()
    {
        // Given «Чаевые за лёгкую» for the season and «не выше лёгкой»: met on easy gives the coin, unmet on hard does not
        var s = RunConditionTests.Season(pack: PackWith());
        s.Give("Вася", "easy-tip");
        Vowed(s, "Вася", "vow-easy");
        Play(s);
        s.NextRandom(1, 2).Complete("Вася", Difficulty.Easy);
        Assert.Single(s.LastEvents<CoinsChanged>(), c => c.Reason == CoinsReason.Item);

        s.Advance(TimeSpan.FromHours(1));
        Vowed(s, "Вася", "vow-easy");
        Play(s, "Silent Hill");
        s.Complete("Вася", Difficulty.Hard);

        ScenarioAssert.Accepted(s);
        Assert.False(Assert.Single(s.LastEvents<RunConditionChecked>()).Met);
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), c => c.Reason == CoinsReason.Item);
    }

    [Fact]
    public void Difficulty_at_most_holds_in_the_condition_of_a_trigger()
    {
        // «Чаевые за лёгкую» without any run condition: a normal completion is above easy, no coin
        var s = RunConditionTests.Season(pack: PackWith());
        s.Give("Вася", "easy-tip");
        Play(s);
        s.NextRandom(1, 2).Complete("Вася", Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), c => c.Reason == CoinsReason.Item);
    }

    [Fact]
    public void With_events_switched_off_a_condition_waits_instead_of_being_lost()
    {
        // Given «не ниже сложной» waits, then events are switched off (items stay on)
        var s = RunConditionTests.Season(pack: PackWith());
        Vowed(s, "Вася", "vow-hard");
        s.Act(new ChangeRuleset(s.Ruleset with { Features = s.Ruleset.Features with { Events = false } }));
        ScenarioAssert.Accepted(s);

        // When he rolls, starts and completes on normal: no condition, dice as usual
        var runId = Play(s);
        s.NextRandom(1, 2).Complete("Вася", Difficulty.Normal);
        Assert.Empty(s.State.Runs[runId].Snapshot.Conditions);
        Assert.Empty(s.LastEvents<RunConditionChecked>());

        // Then the condition still waits, and with events on again the next roll takes it
        Assert.Equal("vow-hard", Assert.Single(s.Player("Вася").Wallet.NextRoll).ObjectId);
        s.Advance(TimeSpan.FromHours(1));
        s.Act(new ChangeRuleset(s.Ruleset with { Features = s.Ruleset.Features with { Events = true } }));
        s.Roll("Вася");
        Assert.Equal("vow-hard", Assert.Single(s.Player("Вася").Offer!.Snapshot.Conditions).ObjectId);
    }
}
