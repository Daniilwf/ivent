using System.Text.Json;
using System.Text.Json.Nodes;
using FsCheck.Xunit;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rolls;

public sealed class RollRevealTests
{
    [Theory]
    [InlineData(1, "game-rolled")]
    [InlineData(2, "game-choice-rolled")]
    public void The_roll_stores_the_game_wheel_and_reveal_deadline_in_its_v3_event(int choiceCount, string type)
    {
        var scenario = TwoGames(choiceCount);
        var rolledAt = scenario.Clock.UtcNow;

        scenario.Roll("Вася");

        var rolled = Assert.Single(scenario.Last.Events, evt => evt is GameRolled or GameChoiceRolled);
        var stored = EventCodec.Encode(rolled);
        Assert.Equal(type, stored.Type);
        Assert.Equal(3, stored.Version);
        using var json = JsonDocument.Parse(stored.Data);
        var data = json.RootElement;
        var wheelGames = data.GetProperty("wheelGames").EnumerateArray().Select(game => game.GetGuid());
        Assert.Equal(new[] { scenario.GameId("Silent Hill"), scenario.GameId("Alan Wake") }.Order(), wheelGames.Order());
        var reveal = data.GetProperty("reveal");
        Assert.NotEqual(Guid.Empty, reveal.GetProperty("id").GetGuid());
        Assert.Equal(rolledAt.AddMinutes(2), reveal.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(rolled, EventCodec.Decode(stored));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void The_owner_shows_the_saved_result_once_without_spending_releasing_or_rolling_again(int choiceCount)
    {
        var scenario = TwoGames(choiceCount).Roll("Вася");
        var playerId = scenario.PlayerId("Вася");
        var reveal = RevealOf(scenario);
        var before = scenario.Player("Вася");
        var reservations = PoolStats.Taken(scenario.State, Guid.Empty).ToArray();
        Assert.Equal(choiceCount, reservations.Length);
        Assert.All(reservations, item => Assert.Equal(playerId, item.ByPlayerId));
        Assert.Equal(reveal.ExpiresAt, Timers.Next(scenario.State, before));
        scenario.Random.Enqueue(0, 0);

        scenario.Act(new ShowRolledGame(playerId, reveal.Id));

        ScenarioAssert.Accepted(scenario);
        Assert.Equal(new RollGameShown(playerId, reveal.Id), Assert.Single(scenario.Last.Events));
        var after = scenario.Player("Вася");
        Assert.Equal(TurnPhase.Rolling, after.Phase);
        Assert.Equal(before.Offer?.GameId, after.Offer?.GameId);
        Assert.Equal(before.Offer?.Snapshot, after.Offer?.Snapshot);
        Assert.Equal(before.Offer?.RolledAt, after.Offer?.RolledAt);
        Assert.Equal(before.Choice?.ChoiceId, after.Choice?.ChoiceId);
        Assert.Equal(
            before.Choice?.Options.Select(option => (option.Id, option.Game?.GameId, option.Game?.Snapshot)),
            after.Choice?.Options.Select(option => (option.Id, option.Game?.GameId, option.Game?.Snapshot)));
        Assert.Equal(before.Wallet, after.Wallet);
        Assert.Equal(before.Coins, after.Coins);
        Assert.Equal(before.RerollsThisRoll, after.RerollsThisRoll);
        Assert.Equal(reservations, PoolStats.Taken(scenario.State, Guid.Empty));
        Assert.Empty(scenario.State.Runs);
        Assert.Equal(2, scenario.Random.ScriptedLeft);
        Assert.Null(Timers.Next(scenario.State, after));
        Assert.Equal(scenario.State, SeasonEngine.Replay(scenario.StoredLog));

        LeavesStateUnchanged(scenario, new ShowRolledGame(playerId, reveal.Id));
    }

    [Fact]
    public void Someone_elses_reveal_id_cannot_reveal_either_players_roll()
    {
        var scenario = TwoGames().Roll("Вася");
        var first = RevealOf(scenario);
        scenario.Roll("Петя");
        var second = RevealOf(scenario);
        Assert.NotEqual(first.Id, second.Id);

        LeavesStateUnchanged(scenario, new ShowRolledGame(scenario.PlayerId("Петя"), first.Id));

        Assert.Equal(first.ExpiresAt, Timers.Next(scenario.State, scenario.Player("Вася")));
        Assert.Equal(second.ExpiresAt, Timers.Next(scenario.State, scenario.Player("Петя")));
    }

    [Fact]
    public void A_late_show_from_an_old_tab_cannot_disclose_the_replacement_roll()
    {
        var scenario = TwoGames().Roll("Вася");
        var player = scenario.PlayerId("Вася");
        var old = RevealOf(scenario);
        scenario.Advance(TimeSpan.FromSeconds(5)).Act(new Reroll(player));
        ScenarioAssert.Accepted(scenario);
        var replacement = RevealOf(scenario);
        Assert.NotEqual(old.Id, replacement.Id);

        LeavesStateUnchanged(scenario, new ShowRolledGame(player, old.Id));

        Assert.Equal(replacement.ExpiresAt, Timers.Next(scenario.State, scenario.Player("Вася")));
        scenario.Act(new ShowRolledGame(player, replacement.Id));
        Assert.Equal(new RollGameShown(player, replacement.Id), Assert.Single(scenario.Last.Events));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void The_timer_shows_an_unseen_roll_at_its_deadline_even_in_closing(int choiceCount)
    {
        var scenario = TwoGames(choiceCount);
        var rolledAt = scenario.Clock.UtcNow;
        var seasonDeadline = rolledAt.AddMinutes(1);
        scenario.Act(new SetSeasonDeadline(seasonDeadline));
        ScenarioAssert.Accepted(scenario);
        scenario.Roll("Вася");
        var reveal = RevealOf(scenario);
        Assert.Equal(rolledAt.AddMinutes(2), reveal.ExpiresAt);

        scenario.Advance(seasonDeadline - scenario.Clock.UtcNow).Act(new ReachDeadline());
        ScenarioAssert.Accepted(scenario);
        Assert.Equal(SeasonStatus.Closing, scenario.State.Status);
        Assert.Equal(reveal.ExpiresAt, Timers.Next(scenario.State, scenario.Player("Вася")));
        var replayed = SeasonEngine.Replay(scenario.StoredLog);
        Assert.Equal(reveal.ExpiresAt, Timers.Next(replayed, replayed.Players[scenario.PlayerId("Вася")]));
        scenario.Advance(reveal.ExpiresAt - scenario.Clock.UtcNow - TimeSpan.FromTicks(1));
        LeavesStateUnchanged(scenario, new FireTimers());

        scenario.Advance(TimeSpan.FromTicks(1)).Act(new FireTimers());

        ScenarioAssert.Accepted(scenario);
        Assert.Equal(new RollGameShown(scenario.PlayerId("Вася"), reveal.Id), Assert.Single(scenario.Last.Events));
        Assert.Equal(SeasonStatus.Closing, scenario.State.Status);
        Assert.Null(Timers.Next(scenario.State, scenario.Player("Вася")));
        Assert.Equal(scenario.State, SeasonEngine.Replay(scenario.StoredLog));
        LeavesStateUnchanged(scenario, new FireTimers());
        LeavesStateUnchanged(scenario, new ShowRolledGame(scenario.PlayerId("Вася"), reveal.Id));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void Older_roll_events_have_no_game_wheel_or_pending_reveal(int choiceCount, int version)
    {
        var rolled = EventCodec.Encode(LastRoll(TwoGames(choiceCount).Roll("Вася")));
        var data = JsonNode.Parse(rolled.Data)!.AsObject();
        Assert.True(data.Remove("wheelGames"));
        Assert.True(data.Remove("reveal"));
        if (version == 1)
        {
            Assert.True(data.Remove("sectors"));
        }

        var legacy = EventCodec.Decode(new StoredEvent(rolled.Type, version, data.ToJsonString()));

        if (choiceCount == 1)
        {
            var plain = Assert.IsType<GameRolled>(legacy);
            Assert.Null(plain.WheelGames);
            Assert.Null(plain.Reveal);
            Assert.Equal(["Horror"], plain.Sectors);
        }
        else
        {
            var choice = Assert.IsType<GameChoiceRolled>(legacy);
            Assert.Null(choice.WheelGames);
            Assert.Null(choice.Reveal);
            Assert.Equal(["Horror"], choice.Sectors);
        }

        var scenario = TwoGames(choiceCount).AppendCraftedEvents(legacy).Advance(TimeSpan.FromMinutes(3));
        Assert.Null(Timers.Next(scenario.State, scenario.Player("Вася")));
        LeavesStateUnchanged(scenario, new FireTimers());
        Assert.Equal(choiceCount, PoolStats.Taken(scenario.State, Guid.Empty).Count);
    }

    [Fact]
    public void A_large_game_wheel_keeps_twelve_different_games_from_the_picked_category_including_the_result()
    {
        var scenario = TwoGames();
        var eligible = new List<Guid> { scenario.GameId("Silent Hill"), scenario.GameId("Alan Wake") };
        for (var index = 0; index < 15; index++)
        {
            var title = $"Horror {index}";
            scenario.WithGame(title, 5, "Horror");
            eligible.Add(scenario.GameId(title));
        }

        scenario.WithDeletedGame("Removed", 5, "Horror").WithGame("Other category", 5, "Puzzle").Roll("Вася");

        var rolled = Assert.Single(scenario.LastEvents<GameRolled>());
        var games = rolled.WheelGames ?? [];
        Assert.Equal(12, games.Count);
        Assert.Equal(12, games.Distinct().Count());
        Assert.Contains(rolled.GameId, games);
        Assert.All(games, game => Assert.Contains(game, eligible));
    }

    [Fact]
    public void The_game_wheel_keeps_the_logged_miss_and_the_offer_as_distinct_sectors()
    {
        var scenario = TwoGames().RollTitle("Петя", "Silent Hill");
        scenario.Act(new ShowRolledGame(scenario.PlayerId("Петя"), RevealOf(scenario).Id));
        ScenarioAssert.Accepted(scenario);
        scenario.Start("Петя");

        scenario.NextRandom(0, 0, 0).Roll("Вася");

        var rolled = Assert.Single(scenario.LastEvents<GameRolled>());
        var missed = scenario.GameId("Silent Hill");
        var offered = scenario.GameId("Alan Wake");
        Assert.Equal(new RollMiss(missed, RollMissReason.BeingPlayed, scenario.PlayerId("Петя")), Assert.Single(rolled.Misses));
        Assert.Equal(offered, rolled.GameId);
        Assert.Equal(new[] { missed, offered }.Order(), (rolled.WheelGames ?? []).Order());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void The_saved_game_wheel_omits_hidden_foreign_reservations_but_the_log_keeps_the_misses(int choiceCount)
    {
        var scenario = TwoGames(choiceCount)
            .WithGame("Outlast", 6, "Horror")
            .WithGame("Dead Space", 7, "Horror");
        var other = scenario.PlayerId("Петя");
        var owner = scenario.PlayerId("Вася");
        scenario.NextRandom(new int[choiceCount + 1]).Roll("Петя");
        var hidden = RevealOf(scenario);
        var reserved = PoolStats.Taken(scenario.State, owner).Where(m => m.ByPlayerId == other)
            .Select(m => m.GameId).Order().ToArray();
        Assert.Equal(choiceCount, reserved.Length);

        scenario.NextRandom(new int[2 * choiceCount + 1]).Roll("Вася");
        var rolled = LastRoll(scenario);
        var (misses, wheel) = rolled switch
        {
            GameRolled plain => (plain.Misses, plain.WheelGames),
            GameChoiceRolled choice => (choice.Misses, choice.WheelGames),
            _ => throw new InvalidOperationException("Expected a roll result."),
        };
        Assert.Equal(reserved, misses.Select(m => m.GameId).Order());
        Assert.All(misses, m =>
        {
            Assert.Equal(other, m.ByPlayerId);
            Assert.Equal(RollMissReason.BeingPlayed, m.Reason);
        });
        Assert.Equal(4 - choiceCount, (wheel ?? []).Count);
        Assert.DoesNotContain(wheel ?? [], reserved.Contains);

        scenario.Act(new ShowRolledGame(other, hidden.Id));
        ScenarioAssert.Accepted(scenario);
        var saved = Assert.Single(scenario.StoredLog, e => e is GameRolled plain && plain.PlayerId == owner
            || e is GameChoiceRolled choice && choice.PlayerId == owner);
        Assert.Equal(EventCodec.Encode(rolled), EventCodec.Encode(saved));
    }

    [Property(MaxTest = 80)]
    public void Revealing_a_roll_does_not_change_the_following_draw_or_release_its_reservation(int seed)
    {
        var hidden = SeveralGames(seed).Roll("Вася");
        var displayed = SeveralGames(seed).Roll("Вася");
        var first = Assert.Single(hidden.LastEvents<GameRolled>());
        Assert.Equal(first.GameId, Assert.Single(displayed.LastEvents<GameRolled>()).GameId);
        displayed.Act(new ShowRolledGame(displayed.PlayerId("Вася"), RevealOf(displayed).Id));
        ScenarioAssert.Accepted(displayed);

        hidden.Roll("Петя");
        displayed.Roll("Петя");

        var hiddenSecond = Assert.Single(hidden.LastEvents<GameRolled>());
        var displayedSecond = Assert.Single(displayed.LastEvents<GameRolled>());
        Assert.NotEqual(first.GameId, hiddenSecond.GameId);
        Assert.Equal(hiddenSecond.GameId, displayedSecond.GameId);
        Assert.Equal(hiddenSecond.Misses, displayedSecond.Misses);
    }

    private static void LeavesStateUnchanged(Scenario scenario, ICommand command)
    {
        var before = scenario.State;
        var logLength = scenario.Log.Count;
        scenario.Act(command);
        Assert.Empty(scenario.Last.Events);
        Assert.Equal(before, scenario.State);
        Assert.Equal(logLength, scenario.Log.Count);
    }

    private static IGameEvent LastRoll(Scenario scenario) =>
        Assert.Single(scenario.Last.Events, evt => evt is GameRolled or GameChoiceRolled);

    private static RollReveal RevealOf(Scenario scenario) => Assert.IsType<RollReveal>(LastRoll(scenario) switch
    {
        GameRolled plain => plain.Reveal,
        GameChoiceRolled choice => choice.Reveal,
        _ => null,
    });

    private static Scenario SeveralGames(int seed) => TwoGames(seed: seed)
        .WithGame("Dead Space", 7, "Horror")
        .WithGame("Phasmophobia", 3, "Horror")
        .WithGame("Signalis", 8, "Horror")
        .WithGame("Outlast", 6, "Horror");

    private static Scenario TwoGames(int choiceCount = 1, int seed = 42) =>
        Scenario.New(seed: seed)
            .WithRuleset(rules => rules with { Roll = rules.Roll with { ChoiceCount = choiceCount } })
            .WithCategory("Horror")
            .WithGame("Silent Hill", 12, "Horror")
            .WithGame("Alan Wake", 15, "Horror")
            .WithPlayers("Вася", "Петя");
}
