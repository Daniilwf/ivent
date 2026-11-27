using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Seasons;

public sealed class SeasonTemplateTests
{
    [Fact]
    public void A_draft_replays_from_its_own_events_without_the_source_season()
    {
        var source = Scenario.New().WithPlayers("Вася");
        source.WithRuleset(rules => rules with { Map = rules.Map with { LinearLength = rules.Map.LinearLength + 1 } });
        source.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        source.Act(new ChangeSeasonStatus(SeasonStatus.Finished));
        Assert.Equal(SeasonStatus.Finished, source.State.Status);

        var request = new CreateSeasonFromTemplate(SequentialIds.Make(0x30000000, 2), "Новый сезон", source.State.SeasonId);
        var context = new EngineContext(source.Clock, source.Random, source.Ids, new PoolSnapshot([], []))
        {
            TemplateSeason = source.State,
        };
        var result = SeasonEngine.Execute(SeasonState.Empty, request, context);

        Assert.True(result.IsAccepted, result.Rejection?.Detail);
        Assert.Equal(SeasonStatus.Draft, result.State.Status);
        Assert.Equal(source.State.Rules, result.State.Rules);
        Assert.Equal(source.State.Map, result.State.Map);
        Assert.Empty(result.State.Players);
        Assert.Null(result.State.Result);

        var recorded = EventCodec.Decode(EventCodec.Encode(Assert.Single(result.Events)));
        Assert.Equal(result.State, SeasonEngine.Apply(SeasonState.Empty, recorded));
    }

    [Fact]
    public void A_snapshot_of_a_different_season_cannot_be_used()
    {
        var source = Scenario.New().WithPlayers("Вася");
        var request = new CreateSeasonFromTemplate(SequentialIds.Make(0x30000000, 2), "Новый сезон", SequentialIds.Make(0x30000000, 3));
        var context = new EngineContext(source.Clock, source.Random, source.Ids, new PoolSnapshot([], []))
        {
            TemplateSeason = source.State,
        };

        var result = SeasonEngine.Execute(SeasonState.Empty, request, context);

        Assert.Equal(RejectionCodes.SeasonTemplateUnknown, result.Rejection?.Code);
        Assert.Empty(result.Events);
        Assert.Equal(SeasonState.Empty, result.State);
    }
}
