using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.GlobalEvents;

/// <summary>
/// Shared setup of the task 5.9 tests (SPEC «Общие ивенты», «Цели», «Уточнения правил»: hours of a global goal are the
/// HowLongToBeat hours; D-517, D-518, D-519, D-537, D-700, D-750…D-755): a season with global events on, one category of
/// games of known hours (so <see cref="Scenario.RollTitle"/> picks any of them), Вася, Петя, Маша and Коля, and a pack of
/// events for everyone. Written by <c>test-author</c> from the SPEC and the public contract of
/// <c>GameEvent.Engine.GlobalEvents</c>, before the implementation is finished.
/// </summary>
internal static class GlobalSetup
{
    public const string Comment = "Проверка пруфа";

    public static readonly string[] Names = ["Вася", "Петя", "Маша", "Коля"];

    /// <summary>The games of the season with their HowLongToBeat hours; «Без часов» has none (the player's estimate counts, D-755).</summary>
    public static readonly (string Title, decimal? Hours)[] Games =
    [
        ("Silent Hill", 10), ("Alan Wake", 4), ("Dead Space", 6), ("Outlast", 6), ("Soma", 6), ("Prey", 6), ("Doom", 6),
        ("Quake", 6), ("Limbo", 2), ("Inside", 6), ("Amnesia", 6), ("Без часов", null),
    ];

    public static readonly TargetSpec All = new() { Selector = TargetSelector.All };

    public static ChangeResourceAction Coins(int amount) => new() { Resource = "coins", Amount = ContentValue.Of(amount) };

    public static ChangeResourceAction Points(int amount) => new() { Resource = "points", Amount = ContentValue.Of(amount) };

    public static GiveObjectAction Give(string objectId) => new() { ObjectId = objectId };

    public static EffectSpec OnAll(params ActionSpec[] actions) => new() { Target = All, Actions = [.. actions] };

    public static ObjectDefinition Event(string id, EffectSpec? effect, bool manual = false) => new()
    {
        Id = id,
        Kind = ObjectKind.Event,
        Name = $"Ивент {id}",
        Description = "Ивент для всех.",
        Rarity = Rarity.Common,
        Manual = manual,
        Effect = effect,
    };

    /// <summary>«Монетный дождь»: everyone +2 coins.</summary>
    public static readonly ObjectDefinition CoinRain = Event("coin-rain", OnAll(Coins(2)));

    /// <summary>Points and coins only: a later finisher gets it too (D-700).</summary>
    public static readonly ObjectDefinition BonusDay = Event("bonus-day", OnAll(Coins(2), Points(1)));

    /// <summary>Coins and an object: not for a later finisher (D-700).</summary>
    public static readonly ObjectDefinition GiftRain = Event("gift-rain", OnAll(Coins(2), Give("orange")));

    /// <summary>An effect with a term of its own (D-517: «выдать всем эффект со сроком { hours: N }»).</summary>
    public static readonly ObjectDefinition DayShield = new()
    {
        Id = "day-shield",
        Kind = ObjectKind.Effect,
        Name = "Щит на сутки",
        Description = "Гасит враждебные эффекты сутки.",
        Rarity = Rarity.Epic,
        Effect = new EffectSpec { Trigger = Trigger.HostileIncoming, Intercept = Intercept.Hostile, Duration = new DurationSpec { Hours = 24 }, Actions = [] },
    };

    /// <summary>Everyone gets <see cref="DayShield"/>.</summary>
    public static readonly ObjectDefinition ShieldDay = Event("shield-day", OnAll(Give("day-shield")));

    /// <summary>A target of <c>self</c>: in a global event, each one himself (D-752).</summary>
    public static readonly ObjectDefinition SelfCoins = Event("self-coins", new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Self }, Actions = [Coins(3)] });

    /// <summary>No target written: as <c>all</c> (D-752).</summary>
    public static readonly ObjectDefinition PlainCoins = Event("plain-coins", new EffectSpec { Actions = [Coins(1)] });

    /// <summary>A banner and a goal only: no effect at all, so manual (a pack refuses an object without an effect otherwise; D-752).</summary>
    public static readonly ObjectDefinition Banner = Event("banner", null, manual: true);

    /// <summary>The objects every season of these tests has: the events above, the CONTENT.md examples they use.</summary>
    public static ObjectDefinition[] BaseObjects()
    {
        var examples = EconomyScenario.Examples();
        return
        [
            CoinRain, BonusDay, GiftRain, DayShield, ShieldDay, SelfCoins, PlainCoins, Banner,
            examples["orange"], examples["forced-genre"], examples["karaoke"], examples["horror-week"], examples["tailwind"],
            examples["gamble"], examples["coin-or-step"],
        ];
    }

    /// <summary>A pack of <see cref="BaseObjects"/> and <paramref name="extra"/> objects, with <paramref name="decks"/>.</summary>
    public static ContentPack Pack(ObjectDefinition[]? extra = null, params WheelDefinition[] decks) => new()
    {
        Objects = [.. BaseObjects().Concat(extra ?? []).OrderBy(o => o.Id, StringComparer.Ordinal)],
        Wheels = [.. decks.OrderBy(d => d.Id, StringComparer.Ordinal)],
    };

    public static Ruleset Rules(int maxActive = 2, bool polls = false)
    {
        var rules = TestRuleset.Create().WithGlobalEvents(maxActive);
        return polls ? rules.WithPolls() : rules;
    }

    /// <summary>
    /// The season of the global event tests: map length <paramref name="mapLength"/> (4 lets a 6-hour completion of 3 and 1
    /// finish, <see cref="FinishWith"/>), the games of <see cref="Games"/> in «Horror», the players, the pack published.
    /// </summary>
    public static Scenario Season(Ruleset? rules = null, int mapLength = 100, ContentPack? pack = null, string[]? players = null, int seed = 42)
    {
        var s = Scenario.New(rules ?? Rules(), seed).WithMapLength(mapLength).WithCategory("Horror");
        foreach (var (title, hours) in Games)
        {
            s.WithGame(title, hours, "Horror");
        }

        s.WithPlayers(players ?? Names);
        return s.WithContent(pack ?? Pack());
    }

    public static GlobalEventGoal HoursGoal(int target, EffectSpec? reward = null) => new() { Metric = GoalMetric.Hours, Target = target, Reward = reward };

    public static GlobalEventGoal RunsGoal(int target, EffectSpec? reward = null) => new() { Metric = GoalMetric.Runs, Target = target, Reward = reward };

    /// <summary>The admin launches a global event; it must be accepted. Returns its <see cref="GlobalEventLaunched"/>.</summary>
    public static GlobalEventLaunched Launch(this Scenario s, string objectId = "coin-rain", int hours = 72, GlobalEventGoal? goal = null)
    {
        s.Act(new LaunchGlobalEvent(objectId, hours, goal));
        ScenarioAssert.Accepted(s);
        return Assert.Single(s.LastEvents<GlobalEventLaunched>());
    }

    /// <summary>The global event <paramref name="id"/> as the season holds it.</summary>
    public static GlobalEventState Global(this Scenario s, Guid id) => s.State.GlobalEvents[id];

    /// <summary>Runs the pending settlements for everyone, as the queue does after a command (D-702).</summary>
    public static Scenario SettleAll(this Scenario s)
    {
        for (var i = 0; ForAllSettlements.Next(s.State) is { } id; i++)
        {
            Assert.True(i < 20, "The settlement never ends.");
            s.Act(new SettleForAll(id));
            ScenarioAssert.Accepted(s);
        }

        return s;
    }

    /// <summary>The number of d4 a completion of <paramref name="hours"/> throws on the test ruleset (3 hours a die, nearest, 1…10).</summary>
    public static int DiceFor(decimal hours) => Math.Clamp((int)Math.Round(hours / 3, MidpointRounding.AwayFromZero), 1, 10);

    /// <summary>
    /// The player rolls <paramref name="title"/>, starts it, and plays two hours (past <c>roll.minPlayMinutesBeforeDrop</c>).
    /// Returns the run.
    /// </summary>
    public static Guid Playing(this Scenario s, string player, string title)
    {
        s.RollTitle(player, title).Start(player);
        s.Advance(TimeSpan.FromHours(2));
        return s.Player(player).ActiveRunId!.Value;
    }

    /// <summary>
    /// The player completes the run he plays, every die a 1 (<paramref name="hours"/> — the hours its dice are thrown by);
    /// must be accepted.
    /// </summary>
    public static Scenario CompleteOnes(this Scenario s, string player, decimal hours, decimal? estimatedHours = null, string? hoursSource = null)
    {
        s.NextRandom([.. Enumerable.Repeat(1, DiceFor(hours))]).Complete(player, Difficulty.Normal, estimatedHours, hoursSource);
        ScenarioAssert.Accepted(s);
        return s;
    }

    /// <summary>The player plays <paramref name="title"/> and completes it with ones; returns the run.</summary>
    public static Guid Completed(this Scenario s, string player, string title, decimal? estimatedHours = null)
    {
        var hours = Games.Single(g => g.Title == title).Hours ?? estimatedHours ?? throw new ArgumentException("A game without hours needs an estimate.");
        var run = s.Playing(player, title);
        s.CompleteOnes(player, hours, estimatedHours, Games.Single(g => g.Title == title).Hours is null ? "https://howlongtobeat.com/game/1" : null);
        return run;
    }

    /// <summary>On a map of 4: the player completes the 6-hour <paramref name="title"/> with 3 and 1 and finishes. Returns the run.</summary>
    public static Guid FinishWith(this Scenario s, string player, string title)
    {
        var run = s.Playing(player, title);
        s.NextRandom(3, 1).Complete(player);
        ScenarioAssert.Accepted(s);
        Assert.NotNull(s.Player(player).Finish);
        s.Advance(TimeSpan.FromHours(1));
        return run;
    }

    /// <summary>The first <see cref="ForAllQueued"/> of the last command.</summary>
    public static ForAllQueued Queued(this Scenario s) => Assert.Single(s.LastEvents<ForAllQueued>());

    public static int Oranges(this Scenario s, string player) => s.Player(player).Wallet.Inventory.Count(o => o.ObjectId == "orange");

    public static int Count(this Scenario s, string player, string objectId) => s.Player(player).Wallet.Inventory.Count(o => o.ObjectId == objectId);
}
