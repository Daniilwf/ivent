using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Rulesets;

/// <summary>
/// The admin changes the season's rules. The new ruleset gets the next version; runs already rolled keep
/// their snapshot (S1, S2). The map is fixed at creation: <c>map.linearLength</c> does not rebuild it.
/// <paramref name="ExpectedVersion"/> is the version the admin edited: if someone saved in between, the change is
/// refused instead of silently overwriting theirs; null skips the check (scripts, tests).
/// </summary>
public sealed record ChangeRuleset(Ruleset Ruleset, int? ExpectedVersion = null) : ICommand;

/// <summary>A new version of the season's rules, stored whole: history «было/стало» is a diff of consecutive events.</summary>
[EventType("ruleset-changed")]
public sealed record RulesetChanged(int Version, Ruleset Ruleset) : IGameEvent;

internal static class RulesetChanges
{
    public static Decision Decide(SeasonState state, ChangeRuleset command)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (command.ExpectedVersion is { } expected && expected != state.RulesetVersion)
        {
            return Decision.Reject(
                RejectionCodes.RulesetVersionConflict,
                $"The rules were edited from version {expected}, but version {state.RulesetVersion} is in force now.");
        }

        if (RulesetValidator.Check(command.Ruleset) is { } rejection)
        {
            return rejection;
        }

        // D-301: the chain of a linear season is a valid graph, so it may switch to the graph mode; a graph never
        // silently turns into a chain.
        if (state.Rules.Features.MapMode == MapMode.Graph && command.Ruleset.Features.MapMode != MapMode.Graph)
        {
            return Decision.Reject(RejectionCodes.MapModeFixed, "A season on a graph map stays on it.");
        }

        // D-302: the map's event and shop cells need their mechanics
        if (Map.MapValidator.Validate(state.Map, command.Ruleset).FirstOrDefault(e => e.Code == Map.MapErrorCodes.FeatureDisabled) is { } off)
        {
            return Decision.Reject(RejectionCodes.RulesetInvalid, $"Cell {off.Subject} of the map needs its mechanic: {off.Message}");
        }

        return command.Ruleset == state.Ruleset
            ? Decision.Reject(RejectionCodes.RulesetUnchanged, "The new ruleset equals the current one.")
            : Decision.Accept(new RulesetChanged(state.RulesetVersion + 1, command.Ruleset));
    }

    public static SeasonState Apply(SeasonState state, RulesetChanged e) =>
        state with { Ruleset = e.Ruleset, RulesetVersion = e.Version };
}
