using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Finish;
using GameEvent.Engine.Turns;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Undo;

/// <summary>
/// What a command touched, by which later commands depend on it (D-20, D-104): a change of a player also touches the
/// games they hold — the offer and a choice's options — and the finish order when they have finished; a change of a
/// finisher's run touches the finish order; a manual effect is touched by its id. Found by mutation testing (D-282):
/// the scenario tests reach these keys only together with others.
/// </summary>
public class UndoKeysTests
{
    private const string FinishOrder = "finishOrder";

    private static SeasonState Touch(SeasonState state, Guid playerId) =>
        state with { Players = state.Players.SetItem(playerId, state.Players[playerId] with { Coins = state.Players[playerId].Coins + 1 }) };

    [Fact]
    public void A_player_change_touches_the_offered_game()
    {
        var s = FinishSetup.New();
        s.Roll("Вася");
        var offer = s.Player("Вася").Offer!;

        var keys = Undoing.Keys(s.State, Touch(s.State, s.PlayerId("Вася")), []);

        Assert.Contains($"game:{offer.GameId}", keys);
        Assert.Contains($"player:{s.PlayerId("Вася")}", keys);
        Assert.DoesNotContain(FinishOrder, keys);
    }

    [Fact]
    public void A_player_change_touches_every_game_of_a_pending_choice()
    {
        var s = FinishSetup.New();
        s.Roll("Вася");
        var vasya = s.PlayerId("Вася");
        var offer = s.Player("Вася").Offer!;
        var choosing = s.State with
        {
            Players = s.State.Players.SetItem(vasya, s.Player("Вася") with
            {
                Offer = null,
                Choice = new PendingChoice(Guid.Empty, ChoiceKind.Game, [new ChoiceOption(offer.GameId.ToString("N"), offer)]),
            }),
        };

        Assert.Contains($"game:{offer.GameId}", Undoing.Keys(choosing, Touch(choosing, vasya), []));
    }

    [Fact]
    public void A_finishers_change_or_a_change_of_their_run_touches_the_finish_order()
    {
        var s = FinishSetup.New();
        var run = FinishSetup.FinishRun(s, "Вася");
        Assert.NotNull(s.Player("Вася").Finish);

        Assert.Contains(FinishOrder, Undoing.Keys(s.State, Touch(s.State, s.PlayerId("Вася")), []));

        var changedRun = s.State with { Runs = s.State.Runs.SetItem(run, s.State.Runs[run] with { Hours = 7 }) };
        var keys = Undoing.Keys(s.State, changedRun, []);
        Assert.Contains($"run:{run}", keys);
        Assert.Contains(FinishOrder, keys);
    }

    [Fact]
    public void Finish_events_touch_the_finish_order_even_without_a_state_change()
    {
        var s = FinishSetup.New();

        Assert.Contains(FinishOrder, Undoing.Keys(s.State, s.State, [new PlayerFrozen(s.PlayerId("Вася"))]));
        Assert.Contains(FinishOrder, Undoing.Keys(s.State, s.State, [new FinishSurplusChanged(s.PlayerId("Вася"), 2)]));
        Assert.Empty(Undoing.Keys(s.State, s.State, []));
    }

    [Fact]
    public void A_manual_effect_is_touched_by_its_id()
    {
        var s = FinishSetup.New();
        var effect = new PendingManualEffect(Guid.Parse("0e000000-0000-0000-0000-000000000001"), s.PlayerId("Вася"), EventKind.Bad, ManualEffectSource.PaidReroll, null);
        var after = s.State with { ManualEffects = s.State.ManualEffects.Add(effect.EffectId, effect) };

        Assert.Equal([$"effect:{effect.EffectId}"], Undoing.Keys(s.State, after, []));
    }

    [Fact]
    public void An_event_touches_its_own_player_and_run_even_through_a_nullable_id()
    {
        var player = Guid.Parse("0a000000-0000-0000-0000-000000000001");
        var run = Guid.Parse("0b000000-0000-0000-0000-000000000001");
        var moved = new Engine.Map.PlayerMoved(player, "start", "c1", 1, ["c1"], Engine.Map.MoveReason.CompletionRoll, run, Paused: false);

        Assert.Equal([("player", player), ("run", run)], GameIds.Own(moved).Order());
        Assert.Equal([("player", player)], GameIds.Own(moved with { RunId = null }));
    }
}
