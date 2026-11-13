using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Players;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Tests.Content;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Polls;

/// <summary>
/// Shared setup of the task 5.8 tests (SPEC «Голосования», D-512…D-516, D-541): a season with polls on, the categories
/// Horror, RPG, Racing with games and Puzzle with a deleted game only, four players, Петя the leader by points, the CONTENT.md
/// object «forced-genre» published. Written by <c>test-author</c> from the SPEC and the public contract of
/// <c>GameEvent.Engine.Polls</c>, before the implementation.
/// </summary>
internal static class PollSetup
{
    public static readonly string[] Names = ["Вася", "Петя", "Маша", "Коля"];

    /// <summary>A user who is not a player of the season: a spectator, or a player of another season.</summary>
    public static readonly Guid Spectator = Guid.Parse("4f000000-0000-0000-0000-000000000001");

    public static string N(Guid id) => id.ToString("N");

    /// <summary>The ruleset with polls on (and items), changed by <paramref name="change"/>.</summary>
    public static Ruleset Rules(Func<Ruleset, Ruleset>? change = null, int gameOptions = 5, bool allowChangeVote = true, bool showLiveCountsDefault = false)
    {
        var rules = TestRuleset.Create().WithPolls(gameOptions, allowChangeVote, showLiveCountsDefault);
        return change is null ? rules : change(rules);
    }

    /// <summary>The ruleset where spectators may vote or not.</summary>
    public static Func<Ruleset, Ruleset> Spectators(bool canVote) => r => r with { Social = r.Social with { SpectatorsCanVote = canVote } };

    /// <summary>
    /// The season of the poll tests. Games of 6 hours (two d4 for a completion), so <paramref name="mapLength"/> 4 lets a
    /// player finish with one completion (<see cref="Finish"/>).
    /// </summary>
    public static Scenario Season(Ruleset? rules = null, int? mapLength = null, int seed = 42, string[]? players = null)
    {
        var s = Scenario.New(rules ?? Rules(), seed);
        if (mapLength is { } length)
        {
            s.WithMapLength(length);
        }

        s.WithCategory("Horror").WithCategory("RPG").WithCategory("Racing").WithCategory("Puzzle")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Alan Wake", 6, "Horror")
            .WithGame("Skyrim", 6, "RPG").WithGame("Persona", 6, "RPG")
            .WithGame("Need for Speed", 6, "Racing")
            .WithDeletedGame("Tetris", 6, "Puzzle")
            .WithPlayers(players ?? Names);
        s.WithContent(EconomyScenario.ExamplePack("forced-genre"));
        return s.WithPoints("Петя", 10);
    }

    /// <summary>The poll of CONTENT.md «Зона, клетки, голосование…» as the admin uploads it.</summary>
    public static PollDefinition ContentPoll() => ContentJson.Parse<PollDefinition>(ContentExamplesTests.Example("poll"));

    /// <summary>A poll without an effect of its result (a nomination): the list, who votes, anonymity.</summary>
    public static PollDefinition ListPoll(PollVoters voters = PollVoters.Players, bool anonymous = false, bool? showLiveCounts = null, params string[] options) =>
        new()
        {
            Question = "Лучший момент недели?",
            Options = new ChoiceOptionsSpec { List = options.Length == 0 ? ["Финиш", "Дроп", "Реролл"] : [.. options] },
            Voters = voters,
            Anonymous = anonymous,
            ClosesInHours = 24,
            ShowLiveCounts = showLiveCounts,
        };

    /// <summary>A poll from <paramref name="from"/> whose result acts by <paramref name="onResult"/>.</summary>
    public static PollDefinition EffectPoll(EffectSpec onResult, ChoiceSource from = ChoiceSource.Categories, PollVoters voters = PollVoters.Players) =>
        new()
        {
            Question = "Что подкинем?",
            Options = new ChoiceOptionsSpec { From = from },
            Voters = voters,
            Anonymous = false,
            ClosesInHours = 24,
            OnResult = onResult,
        };

    /// <summary>A poll with a list whose result acts by <paramref name="onResult"/>.</summary>
    public static PollDefinition ListEffectPoll(EffectSpec onResult, params string[] options) =>
        ListPoll(options: options.Length == 0 ? ["да", "нет"] : options) with { OnResult = onResult };

    public static EffectSpec GenreTo(TargetSelector? selector) => new()
    {
        Target = selector is { } s ? new TargetSpec { Selector = s } : null,
        Actions = [new GiveObjectAction { ObjectId = "forced-genre", Params = new ContentParamDictionary([new("tag", "$result")]) }],
    };

    public static ChangeResourceAction Coins(int amount) => new() { Resource = "coins", Amount = ContentValue.Of(amount) };

    /// <summary>The admin starts the poll; it must be accepted. Returns its <see cref="PollStarted"/>.</summary>
    public static PollStarted Start(this Scenario s, PollDefinition definition)
    {
        s.Act(new StartPoll(definition));
        ScenarioAssert.Accepted(s);
        return Assert.Single(s.LastEvents<PollStarted>());
    }

    /// <summary>The player votes (the result is in <see cref="Scenario.Last"/>).</summary>
    public static Scenario Vote(this Scenario s, Guid pollId, string player, string option) =>
        s.Act(new CastVote(pollId, s.Player(player).UserId, option));

    /// <summary>The player votes; it must be accepted.</summary>
    public static Scenario Voted(this Scenario s, Guid pollId, string player, string option)
    {
        s.Vote(pollId, player, option);
        ScenarioAssert.Accepted(s);
        return s;
    }

    /// <summary>The clock reaches the poll's time and the timer fires.</summary>
    public static Scenario CloseByTimer(this Scenario s, Guid pollId)
    {
        var closesAt = s.State.Polls[pollId].ClosesAt;
        if (s.Clock.UtcNow < closesAt)
        {
            s.Advance(closesAt - s.Clock.UtcNow);
        }

        return s.Act(new FireTimers());
    }

    /// <summary>The player finishes with one completion (map length 4: two d4 showing 3 and 1); returns the run.</summary>
    public static Guid Finish(this Scenario s, string player)
    {
        s.Roll(player).Start(player);
        var runId = s.Player(player).ActiveRunId!.Value;
        s.NextRandom(3, 1).Complete(player);
        ScenarioAssert.Accepted(s);
        Assert.NotNull(s.Player(player).Finish);
        s.Advance(TimeSpan.FromHours(1));
        return runId;
    }

    public static Scenario Inactive(this Scenario s, string player)
    {
        s.Act(new SetPlayerInactive(s.PlayerId(player), true));
        ScenarioAssert.Accepted(s);
        return s;
    }

    /// <summary>Runs the pending settlements for everyone, as the queue does after a command (D-702).</summary>
    public static Scenario SettleAll(this Scenario s)
    {
        for (var i = 0; Engine.Effects.ForAllSettlements.Next(s.State) is { } id; i++)
        {
            Assert.True(i < 20, "The settlement never ends.");
            s.Act(new Engine.Effects.SettleForAll(id));
            ScenarioAssert.Accepted(s);
        }

        return s;
    }

    /// <summary>The objects «forced-genre» the player holds, by their tag.</summary>
    public static IReadOnlyList<string> Genres(this Scenario s, string player) =>
        [.. s.Player(player).Wallet.Inventory.Where(o => o.ObjectId == "forced-genre").Select(o => o.Params!["tag"])];
}
