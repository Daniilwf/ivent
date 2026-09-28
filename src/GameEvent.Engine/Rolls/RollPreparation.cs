using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Rolls;

/// <summary>
/// Special rolls and roll changes (SPEC «Спецролл», D-405). A roll from Idle fires the player's first special roll (in the
/// order they came) and every <c>beforeRoll</c> effect, then fixes all waiting changes for this roll and its rerolls:
/// the special roll's first, then the others in the order they came. Changes that arrive later wait for the next roll —
/// effects of others never touch a roll already made.
/// </summary>
internal static class RollPreparation
{
    public static (IReadOnlyList<IGameEvent> Events, SeasonState State) BeforeRoll(SeasonState state, Guid playerId, EngineContext context)
    {
        if (!state.Rules.Features.Items || Targets.IsFirst(state, playerId))
        {
            return ([], state);
        }

        var waiting = state.Players[playerId].Wallet.NextRoll;
        var events = new List<IGameEvent>();
        foreach (var special in Firing.Subscribed(state, playerId, Trigger.BeforeRoll, ObjectKind.SpecialRoll))
        {
            if (Firing.Fire(state, context, playerId, special, Trigger.BeforeRoll) is { } fired)
            {
                events.AddRange(fired.Events);
                state = fired.State;
                break;
            }
        }

        var (effects, after) = FireEffects(state, playerId, context);
        events.AddRange(effects);
        state = after;

        var wallet = state.Players[playerId].Wallet;
        EquatableArray<RollModifier> modifiers = [.. wallet.NextRoll.Skip(waiting.Count), .. waiting];
        if (modifiers.Count > 0 || wallet.CurrentRoll.Count > 0)
        {
            var applied = new RollModifiersApplied(playerId, modifiers);
            events.Add(applied);
            state = SeasonEngine.Apply(state, applied);
        }

        return (events, state);
    }

    private static (IReadOnlyList<IGameEvent> Events, SeasonState State) FireEffects(SeasonState state, Guid playerId, EngineContext context)
    {
        var events = new List<IGameEvent>();
        foreach (var effect in Firing.Subscribed(state, playerId, Trigger.BeforeRoll, ObjectKind.Effect))
        {
            if (state.Players[playerId].Wallet.Find(effect.InstanceId) is { } held
                && Firing.Fire(state, context, playerId, held, Trigger.BeforeRoll) is { } fired)
            {
                events.AddRange(fired.Events);
                state = fired.State;
            }
        }

        return (events, state);
    }

    /// <summary>The filters of the roll in progress, in their order (priority «effect», D-92).</summary>
    public static IEnumerable<RollFilter> Filters(SeasonPlayer player) =>
        player.Wallet.CurrentRoll
            .Where(m => m.Filter is not null)
            .Select(m => new RollFilter(RollFilterPriority.Effect, $"effect:{m.ObjectId}", game => Rolling.Matches(m.Filter!, game)));

    /// <summary>How many games the roll shows: the rules' count, or more when an item asks for a choice (CONTENT.md «Выбор из трёх»).</summary>
    public static int ChoiceCount(SeasonState state, Guid playerId) =>
        Math.Max(state.Rules.Roll.ChoiceCount, state.Players[playerId].Wallet.CurrentRoll.Max(m => m.ChoiceCount) ?? 0);
}
