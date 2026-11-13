using FsCheck.Xunit;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Events;

/// <summary>
/// Random seasons of task 5.4 (D-505, D-543, EV8): players draw events of every kind that change the next roll and the
/// next throw, use items that do the same, roll, start, complete and drop. Each byte of <c>script</c> is one command, valid
/// or not; the order of the kinds is one of <see cref="s_orders"/> by the seed. After every accepted command: (1) the changes a
/// roll applied (<c>roll-modifiers-applied</c>) are non-decreasing by rank — the kinds by <c>events.applyOrder</c>, an
/// unlisted risky after the listed, the changes of items last; (2) the objects a throw names (<c>run-dice-modified</c>
/// sources) that waited for it are non-decreasing by the same rank; (3) an event's change of the roll is never lost before it
/// applies (a roll, a reroll, «Уже проходил» keep it in the roll in progress or waiting); (4) what is left waiting is an
/// event's filter. At the end the log replays to the state.
/// </summary>
public class AccumulationInvariantTests
{
    private static readonly string[] s_players = ["Вася", "Петя"];

    private static readonly EventKind[]?[] s_orders =
    [
        null,
        [EventKind.Bad, EventKind.Good, EventKind.Special],
        [EventKind.Risky, EventKind.Good, EventKind.Bad, EventKind.Special],
        [EventKind.Good, EventKind.Special, EventKind.Bad, EventKind.Risky],
    ];

    private static readonly string[] s_items = ["sack-special", "sack-good", "sack-bad", "sack-risky", "short-game", "lucky-die", "pick-of-three", "jinx"];

    /// <summary>The rank of a change of <paramref name="kind"/> under <paramref name="order"/> (null — the default).</summary>
    internal static int Rank(EventKind? kind, IReadOnlyList<EventKind>? order)
    {
        order ??= [EventKind.Special, EventKind.Good, EventKind.Bad];
        if (kind is not { } k)
        {
            return order.Count + 1;
        }

        var index = order.ToList().IndexOf(k);
        return index >= 0 ? index : order.Count;
    }

    private static Scenario NewSeason(int seed)
    {
        var order = s_orders[(int)((uint)seed % s_orders.Length)];
        var ruleset = TestRuleset.Create().WithEvents();
        if (order is not null)
        {
            ruleset = ruleset with { Events = ruleset.Events! with { ApplyOrder = new EquatableArray<EventKind>(order) } };
        }

        var s = Scenario.New(ruleset, seed).WithMap(AccumulationTests.LongLine())
            .WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 3, "Horror", "Classic").WithGame("Fatal Frame", 3, "Horror")
            .WithGame("Gothic", 3, "RPG").WithGame("Witcher", 6, "RPG")
            .WithPlayers(s_players);
        return s.WithContent(AccumulationTests.Pack());
    }

    private static ICommand? CommandFor(Scenario s, byte b)
    {
        var player = s.PlayerId(s_players[b % 2]);
        var other = s.PlayerId(s_players[(b + 1) % 2]);
        switch ((b / 2) % 15)
        {
            case 0:
            case 1: return new RollGame(player);
            case 2: return new StartRun(player);
            case 3: return new CompleteRun(player, Difficulty.Normal);
            case 4: return new DropRun(player);
            case 5: return new Reroll(player);
            case 6:
                var offered = s.State.Players[player].Offer?.GameId
                    ?? s.State.Players[player].Choice?.Options.Select(o => o.Game?.GameId).OfType<Guid>().FirstOrDefault();
                return offered is { } game && game != Guid.Empty ? new DeclareAlreadyPlayed(player, game) : null;
            default:
                var objectId = s_items[((b / 2) % 15) - 7];
                s.Act(new AdjustInventory(player, objectId, null, "Для партии"));
                if (!s.Last.IsAccepted)
                {
                    return null;
                }

                var instance = s.LastEvents<ObjectGiven>().Single().Item.InstanceId;
                return new UseItem(player, instance, objectId == "jinx" ? other : null);
        }
    }

    private static void CheckAfter(Scenario s, SeasonState before)
    {
        // (3) An event's change of the roll is never lost before it applies to a game it fits (EV7, D-583): what waited and
        // what the roll in progress held is, after a roll, a reroll or «Уже проходил», in the roll in progress or waiting
        if (s.LastEvents<RollModifiersApplied>().Any() && !s.LastEvents<RunStarted>().Any())
        {
            foreach (var applied in s.LastEvents<RollModifiersApplied>().Select(a => a.PlayerId).Distinct())
            {
                var was = before.Players[applied].Wallet;
                var now = s.State.Players[applied].Wallet;
                var after = now.CurrentRoll.Concat(now.NextRoll).ToList();
                foreach (var change in was.NextRoll.Concat(was.CurrentRoll).Where(m => m.EventKind is not null))
                {
                    Assert.True(after.Remove(change), $"The event's change {change.ObjectId}/{change.EventKind} is lost.");
                }
            }
        }

        // (4) A change left waiting is an event's filter
        foreach (var applied in s.LastEvents<RollModifiersApplied>())
        {
            Assert.All(applied.Waiting.Where(m => !before.Players[applied.PlayerId].Wallet.NextRoll.Contains(m)), m => Assert.True(m is { EventKind: not null, Filter: not null }, $"{m.ObjectId} waits but is not an event's filter."));
        }

        var order = s.Ruleset.Events?.ApplyOrder?.ToList();
        foreach (var applied in s.LastEvents<RollModifiersApplied>())
        {
            var ranks = applied.Modifiers.Select(m => Rank(m.EventKind, order)).ToList();
            Assert.True(
                ranks.Zip(ranks.Skip(1)).All(p => p.First <= p.Second),
                $"Roll changes out of order: {string.Join(", ", applied.Modifiers.Select(m => $"{m.ObjectId}/{m.EventKind}"))}.");
        }

        foreach (var modified in s.LastEvents<RunDiceModified>())
        {
            var waited = before.Players[modified.PlayerId].Wallet.NextDice;
            // A source named twice (one event from two decks) appears where its first, highest-ranked change applies
            var ranks = modified.Mods.Sources
                .Select(id => waited.Where(d => d.ObjectId == id).Select(d => (int?)Rank(d.EventKind, order)).Min())
                .OfType<int>()
                .ToList();
            Assert.True(
                ranks.Zip(ranks.Skip(1)).All(p => p.First <= p.Second),
                $"Throw changes out of order: {string.Join(", ", modified.Mods.Sources)} of {string.Join(", ", waited.Select(d => $"{d.ObjectId}/{d.EventKind}"))}.");
        }
    }

    [Property(MaxTest = 120)]
    public void Applied_changes_follow_the_order_of_kinds_then_items(int seed, byte[] script)
    {
        var s = NewSeason(seed);
        foreach (var b in script ?? [])
        {
            s.Advance(TimeSpan.FromMinutes(41));
            var command = CommandFor(s, b);
            if (command is null)
            {
                continue;
            }

            var before = s.State;
            s.Act(command);
            if (s.Last.IsAccepted)
            {
                CheckAfter(s, before);
            }
        }

        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }
}
