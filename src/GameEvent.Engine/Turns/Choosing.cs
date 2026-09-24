using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Turns;

internal static class Choosing
{
    public static Decision Decide(SeasonState state, MakeChoice command)
    {
        if (TurnRules.Check(state, command.PlayerId, command) is { } rejection)
        {
            return rejection;
        }

        var choice = state.Players[command.PlayerId].Choice!;
        if (choice.ChoiceId != command.ChoiceId)
        {
            // An answer from a stale tab to a choice that is gone.
            return Decision.Reject(RejectionCodes.NoPendingChoice, $"Choice {command.ChoiceId} is not pending.");
        }

        return choice.Options.Any(o => o.Id == command.OptionId)
            ? Decision.Accept(new ChoiceMade(command.PlayerId, command.ChoiceId, command.OptionId))
            : Decision.Reject(RejectionCodes.UnknownChoiceOption, $"'{command.OptionId}' is not an option of choice {command.ChoiceId}.");
    }

    public static SeasonState Apply(SeasonState state, GameChoiceRolled e)
    {
        var choice = new PendingChoice(
            e.ChoiceId, ChoiceKind.Game, [.. e.Offers.Select(o => new ChoiceOption(o.GameId.ToString("N"), o))]);
        return Update(state, e.PlayerId, p => p with { Phase = TurnPhase.Rolling, Offer = null, Choice = choice });
    }

    public static SeasonState Apply(SeasonState state, ChoiceMade e) =>
        Update(state, e.PlayerId, p =>
        {
            var option = p.Choice!.Options.Single(o => o.Id == e.OptionId);
            return p.Choice.Kind switch
            {
                ChoiceKind.Game => p with { Offer = option.Game, Choice = null },
                _ => throw new InvalidOperationException($"Unknown choice kind {p.Choice.Kind}."),
            };
        });

    public static SeasonState Apply(SeasonState state, ChoiceDiscarded e) =>
        Update(state, e.PlayerId, p => p with { Phase = TurnPhase.Idle, Choice = null });

    private static SeasonState Update(SeasonState state, Guid playerId, Func<SeasonPlayer, SeasonPlayer> change) =>
        state with { Players = state.Players.SetItem(playerId, change(state.Players[playerId])) };
}
