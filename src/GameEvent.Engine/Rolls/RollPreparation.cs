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
/// effects of others never touch a roll already made. A player who finished not first rolls without the changes others
/// put on his game before the finish (D-425).
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
        // Conditions of a roll that ended without a game started (an «already played» of the last game, an offer the admin
        // discarded) go on to this one: a condition belongs to the next run, not to a roll (D-591)
        EquatableArray<RollModifier> pending = [.. wallet.CurrentRoll.Where(m => m.RunCondition is not null), .. wallet.NextRoll.Skip(waiting.Count), .. waiting];

        // Others' changes of the game put on him before he finished not first are taken off the roll and recorded by
        // what the roll applies (D-425); dice changes wait for his throw as before.
        EquatableArray<RollModifier> modifiers = Targets.IsLaterFinisher(state, playerId) ? [.. pending.Where(m => !m.Hostile)] : pending;
        if (pending.Count > 0 || wallet.CurrentRoll.Count > 0)
        {
            // A condition contradicting those before it waits for the next roll (D-506)
            var (taken, postponed) = Runs.RunConditions.Split(modifiers, state.Rules.Features.Events);
            var applied = new RollModifiersApplied(playerId, taken) { Waiting = postponed };
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

    /// <summary>
    /// The filters of the roll in progress, in their order (priority «effect», D-92); their tags are an imposed genre,
    /// which refuses the wish reroll (D-325, D-422).
    /// </summary>
    public static IEnumerable<RollFilter> Filters(SeasonPlayer player) =>
        player.Wallet.CurrentRoll
            .Where(m => m.Filter is not null)
            .Select(m => new RollFilter(RollFilterPriority.Effect, $"effect:{m.ObjectId}", game => Rolling.Matches(m.Filter!, game), m.Filter!.Tags ?? default));

    /// <summary>How many games the roll shows: the rules' count, or more when an item asks for a choice (CONTENT.md «Выбор из трёх»).</summary>
    public static int ChoiceCount(SeasonState state, Guid playerId) =>
        state.Rules.Features.Items
            ? Math.Max(state.Rules.Roll.ChoiceCount, state.Players[playerId].Wallet.CurrentRoll.Max(m => m.ChoiceCount) ?? 0)
            : state.Rules.Roll.ChoiceCount;
}
