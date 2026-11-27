using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Reactions;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Reactions;

public sealed class FeedReactionTests
{
    private const string Fire = "🔥";
    private const string Laugh = "😂";
    private static readonly Guid s_spectator = SequentialIds.Make(0x0BAD0000, 1);

    [Fact]
    public void A_reaction_requires_an_enabled_season_an_available_row_and_an_exact_configured_emoji()
    {
        var command = new SetFeedReaction(SequentialIds.Make(0x7C000000, 1), s_spectator, Fire, Selected: true);
        var s = Scenario.New().WithRuleset(rules => rules with { Features = rules.Features with { Reactions = false } });
        Rejects(s, command, available: true, RejectionCodes.FeatureDisabled);

        s.WithPlayers();
        Rejects(s, command, available: false, RejectionCodes.FeatureDisabled);

        s.WithRuleset(rules => rules with { Features = rules.Features with { Reactions = true } });
        Rejects(s, command, available: false, RejectionCodes.FeedReactionTargetUnknown);
        Rejects(s, command with { UserId = Guid.Empty }, available: true, RejectionCodes.FeedReactionInvalidUser);
        Rejects(s, command with { Emoji = Fire + " " }, available: true, RejectionCodes.FeedReactionInvalidEmoji);
    }

    [Fact]
    public void An_archived_public_row_can_receive_a_reaction_without_changing_the_season_result()
    {
        var scenario = Scenario.New()
            .WithRuleset(rules => rules with { Features = rules.Features with { Reactions = true } })
            .WithPlayers("Player");
        var player = scenario.Player("Player");
        scenario.Act(new AdjustPlayer(player.PlayerId, "Adjustment", PointsDelta: 5));
        ScenarioAssert.Accepted(scenario);
        var target = scenario.LastCommandId;
        scenario.MoveStatusTo(SeasonStatus.Archived);
        var standings = scenario.State.Result;
        Assert.NotNull(standings);

        var choose = new SetFeedReaction(target, s_spectator, Fire, Selected: true);
        var commandId = React(scenario, choose);
        Assert.Equal(new FeedReaction(target, s_spectator, Fire), Assert.Single(scenario.State.FeedReactions));
        scenario.Act(new UndoCommand(commandId, "Correction"));
        Assert.Equal(RejectionCodes.SeasonClosed, scenario.Last.Rejection!.Code);

        React(scenario, choose with { Selected = false });
        Assert.Empty(scenario.State.FeedReactions);
        Assert.Equal(standings, scenario.State.Result);
    }

    [Fact]
    public void A_source_undo_and_other_choices_stay_independent_of_the_reaction_and_its_confirmation()
    {
        var s = Scenario.New()
            .WithRuleset(rules => rules with { Features = rules.Features with { Reactions = true } })
            .WithPlayers("Player");
        var anotherRow = s.LastCommandId;
        var player = s.Player("Player");
        s.Act(new AdjustPlayer(player.PlayerId, "Adjustment", PointsDelta: 5));
        ScenarioAssert.Accepted(s);
        var source = s.LastCommandId;
        var choose = new SetFeedReaction(source, s_spectator, Fire, Selected: true);
        var before = s.State;

        var original = React(s, choose);
        var confirmation = React(s, choose);
        React(s, choose with { UserId = player.UserId });
        React(s, choose with { Emoji = Laugh });
        React(s, choose with { TargetCommandId = anotherRow });
        Assert.Equal(before, s.State with { FeedReactions = before.FeedReactions });

        s.Act(new UndoCommand(original, "Correction"));
        Assert.Equal(RejectionCodes.UndoDependents, s.Last.Rejection!.Code);
        Assert.Equal(confirmation, Assert.Single(s.Last.Rejection.Related));

        s.Act(new UndoCommand(source, "Correction"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(player.Points, s.Player("Player").Points);
        Assert.Equal(4, s.State.FeedReactions.Count);

        s.Act(new UndoCommand(confirmation, "Correction"));
        ScenarioAssert.Accepted(s);
        s.Act(new UndoCommand(original, "Correction"));
        ScenarioAssert.Accepted(s);
        Assert.True(s.State.FeedReactions.SetEquals([
            new FeedReaction(source, player.UserId, Fire),
            new FeedReaction(source, s_spectator, Laugh),
            new FeedReaction(anotherRow, s_spectator, Fire),
        ]));
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    private static void Rejects(Scenario s, SetFeedReaction command, bool available, string code)
    {
        var result = SeasonEngine.Execute(s.State, command, s.Context() with { FeedReactionTargetAvailable = available });
        Assert.Equal(code, result.Rejection?.Code);
        Assert.Empty(result.Events);
        Assert.Equal(s.State, result.State);
    }

    private static Guid React(Scenario s, SetFeedReaction command)
    {
        var result = SeasonEngine.Execute(s.State, command, s.Context() with { FeedReactionTargetAvailable = true });
        Assert.True(result.IsAccepted, result.Rejection?.Detail);
        Assert.Equal(
            new FeedReactionSet(command.TargetCommandId, command.UserId, command.Emoji, command.Selected),
            Assert.Single(result.Events));
        s.AppendCraftedEvents([.. result.Events]);
        Assert.Equal(result.State, s.State);
        return s.History[^1].CommandId;
    }
}
