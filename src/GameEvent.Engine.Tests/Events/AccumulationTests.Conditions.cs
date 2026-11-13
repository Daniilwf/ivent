using GameEvent.Engine.Content;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// EV11 with 5.4 (SPEC «Противоречащие условия»: the more important one applies, the other waits for the next game; D-506,
/// D-586): contradicting conditions of events are taken by <c>events.applyOrder</c>, not by the order they came in, and the
/// snapshot keeps the kind of each so a condition given back keeps its place.
/// </summary>
public partial class AccumulationTests
{
    private static ConditionSpec Hard => new() { DifficultyAtLeast = Difficulty.Hard };

    private static ConditionSpec Easy => new() { DifficultyAtMost = Difficulty.Easy };

    [Fact]
    public void A_special_condition_wins_over_an_earlier_contradicting_bad_one()
    {
        // The bad «не ниже сложной» came first, the special «не выше лёгкой» later
        var s = Season();
        Draw(s, "Вася", EventKind.Bad, "cond-hard");
        Draw(s, "Вася", EventKind.Special, "cond-easy");

        s.Roll("Вася");

        Assert.Equal([new RunCondition("cond-easy", Easy, EventKind: EventKind.Special)], s.Player("Вася").Offer!.Snapshot.Conditions);
        Assert.Equal(new (string, EventKind?)[] { ("cond-hard", EventKind.Bad) }, Kinds(Applied(s).Waiting));
        Assert.Equal(new (string, EventKind?)[] { ("cond-hard", EventKind.Bad) }, Kinds(s.Player("Вася").Wallet.NextRoll));

        // The next run takes the waiting one, with its kind
        Plays(s, "Вася").Roll("Вася");
        Assert.Equal([new RunCondition("cond-hard", Hard, EventKind: EventKind.Bad)], s.Player("Вася").Offer!.Snapshot.Conditions);
        Assert.Empty(s.Player("Вася").Wallet.NextRoll);
    }

    [Fact]
    public void The_ruleset_order_decides_which_contradicting_condition_wins()
    {
        var s = Season(Order(EventKind.Bad, EventKind.Good, EventKind.Special));
        Draw(s, "Вася", EventKind.Special, "cond-easy");
        Draw(s, "Вася", EventKind.Bad, "cond-hard");

        s.Roll("Вася");

        Assert.Equal([new RunCondition("cond-hard", Hard, EventKind: EventKind.Bad)], s.Player("Вася").Offer!.Snapshot.Conditions);
        Assert.Equal(new[] { "cond-easy" }, s.Player("Вася").Wallet.NextRoll.Select(m => m.ObjectId));
    }

    [Fact]
    public void Within_a_kind_the_earlier_contradicting_condition_wins()
    {
        var s = Season();
        Draw(s, "Вася", EventKind.Good, "cond-easy");
        Draw(s, "Вася", EventKind.Good, "cond-hard");

        s.Roll("Вася");

        Assert.Equal([new RunCondition("cond-easy", Easy, EventKind: EventKind.Good)], s.Player("Вася").Offer!.Snapshot.Conditions);
        Assert.Equal(new[] { "cond-hard" }, s.Player("Вася").Wallet.NextRoll.Select(m => m.ObjectId));
    }
}
