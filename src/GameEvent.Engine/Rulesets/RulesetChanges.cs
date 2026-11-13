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

        // D-614: the rules a finished season was played by are part of its results, fixed like them
        if (SeasonSetup.IsOver(state))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: results are fixed.");
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

        // D-420: a switched-off mechanic writes nothing, so bets in play would never be settled or taken back by a reject
        if (state.Rules.Features.Bets && !command.Ruleset.Features.Bets && BetsInPlay(state))
        {
            return Decision.Reject(
                RejectionCodes.BetsInPlay, "A bet is open or won on a run whose proof is not approved yet: switch bets off after they are settled.");
        }

        // D-574: the same for the weekly challenge — a reject must still take back a reward of a run not approved yet
        if (state.Rules.Features.WeeklyChallenge && !command.Ruleset.Features.WeeklyChallenge && Challenges.WeeklyChallenges.InPlay(state))
        {
            return Decision.Reject(
                RejectionCodes.WeeklyChallengeInPlay,
                "A weekly challenge is completed with a run whose proof is not approved yet: switch the challenge off after the proofs are checked.");
        }

        // D-730: the same for duels — an open duel would never end, a reject must still take back a win of a run not approved yet
        if (state.Rules.Features.Duels && !command.Ruleset.Features.Duels && Duels.Dueling.InPlay(state))
        {
            return Decision.Reject(
                RejectionCodes.DuelsInPlay,
                "A duel is open or won with a run whose proof is not approved yet: switch duels off after they are settled.");
        }

        // D-685: with events off nobody reviews an IRL proof, and the results wait for it — switch events off after the reviews
        if (state.Rules.Features.Events && !command.Ruleset.Features.Events && Proofs.IrlProofs.Waiting(state) is { Count: > 0 } waiting)
        {
            return Decision.Reject(
                RejectionCodes.IrlProofsInPlay, "An IRL proof waits for the review: switch events off after it is checked.", [.. waiting]);
        }

        // D-751: the same for global events — a running one would never end, a reject must still take a contribution back
        if (GlobalEvents.GlobalEventing.IsOn(state) && !command.Ruleset.Features.GlobalEvents && GlobalEvents.GlobalEventing.InPlay(state))
        {
            return Decision.Reject(
                RejectionCodes.GlobalEventsInPlay,
                "A global event runs, or counts a run whose proof is not approved yet: switch global events off after they end and the proofs are checked.");
        }

        return command.Ruleset == state.Ruleset
            ? Decision.Reject(RejectionCodes.RulesetUnchanged, "The new ruleset equals the current one.")
            : Decision.Accept(new RulesetChanged(state.RulesetVersion + 1, command.Ruleset));
    }

    private static bool BetsInPlay(SeasonState state) =>
        state.Players.Values.SelectMany(p => p.Wallet.Bets).Any(b =>
            b.Status == Inventory.BetStatus.Open
            || (b.Status == Inventory.BetStatus.Won && state.Runs.TryGetValue(b.RunId, out var run)
                && run.Status == Runs.RunStatus.Completed && run.Proof?.Status is not Proofs.ProofStatus.Approved));

    public static SeasonState Apply(SeasonState state, RulesetChanged e) =>
        state with { Ruleset = e.Ruleset, RulesetVersion = e.Version };
}
