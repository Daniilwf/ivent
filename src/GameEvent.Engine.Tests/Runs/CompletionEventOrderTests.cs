using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// The whole completion in one command (D-12, D-96), events exactly in the order
/// <c>RunCompleted, CompletionRolled, PointsChanged, PlayerMoved, CoinsChanged, ManualEffectCreated, RunReviewed</c>.
/// </summary>
public class CompletionEventOrderTests
{
    [Fact]
    public void Full_completion_logs_every_part_in_the_decided_order()
    {
        // Given Вася plays a game without pool hours, challenges on (D-96 (1))
        var s = Scenario.New()
            .WithRuleset(r => r with { Features = r.Features with { Challenges = true } })
            .WithCategory("Horror").WithGame("Silent Hill", null, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");
        ScenarioAssert.Accepted(s);
        var vasya = s.PlayerId("Вася");
        var runId = s.Player("Вася").ActiveRunId!.Value;
        s.Advance(TimeSpan.FromHours(7));
        var now = s.Clock.UtcNow;

        // When he completes it on «выше сложной» with an estimate of 6 hours (2 d6), the challenge (1 d6) and a review
        s.NextRandom(5, 2, 6).Complete(
            "Вася",
            Difficulty.Extreme,
            estimatedHours: 6,
            hoursSource: "https://howlongtobeat.com/game/2231",
            challengeDone: true,
            review: new RunReview(9, "Страшно"));

        // Then
        ScenarioAssert.Accepted(s);
        var events = s.Last.Events;
        Assert.Equal(
            [typeof(RunCompleted), typeof(CompletionRolled), typeof(PointsChanged), typeof(PlayerMoved), typeof(CoinsChanged), typeof(ManualEffectCreated), typeof(RunReviewed)],
            events.Select(e => e.GetType()));

        var effectId = ((ManualEffectCreated)events[5]).EffectId;
        Assert.Equal(
            new IGameEvent[]
            {
                new RunCompleted(runId, vasya, Difficulty.Extreme, 6m, now, "https://howlongtobeat.com/game/2231", ChallengeDone: true),
                new CompletionRolled(runId, vasya, [new Die(6, 5), new Die(6, 2)], [new Die(6, 6)]),
                new PointsChanged(vasya, 13, PointsReason.CompletionRoll, runId),
                new PlayerMoved(vasya, "start", "c13", 13, [.. Enumerable.Range(1, 13).Select(i => $"c{i}")], MoveReason.CompletionRoll, runId),
                new CoinsChanged(vasya, 6, CoinsReason.CompletionReward, runId),
                new ManualEffectCreated(effectId, vasya, EventKind.Good, ManualEffectSource.Difficulty, runId),
                new RunReviewed(runId, vasya, 9, "Страшно", now),
            },
            events);

        // And the state holds all of it
        var run = s.State.Runs[runId];
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal([new Die(6, 5), new Die(6, 2)], run.Dice);
        Assert.Equal([new Die(6, 6)], run.ChallengeDice);
        Assert.Equal("https://howlongtobeat.com/game/2231", run.HoursSource);
        Assert.Equal(new RunReview(9, "Страшно"), run.Review);
        var player = s.Player("Вася");
        Assert.Equal((13, 6, "c13"), (player.Points, player.Coins, player.CellId));
        Assert.True(s.State.ManualEffects.ContainsKey(effectId));
    }

    [Fact]
    public void Plain_completion_logs_points_move_and_coins_only()
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");

        s.Complete("Вася", Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            [typeof(RunCompleted), typeof(CompletionRolled), typeof(PointsChanged), typeof(PlayerMoved), typeof(CoinsChanged)],
            s.Last.Events.Select(e => e.GetType()));
    }
}
