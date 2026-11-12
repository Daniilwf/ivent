using GameEvent.Engine.Content;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Challenges;

/// <summary>
/// The admin sets a weekly challenge (SPEC «Челлендж недели», D-526): a definition of the content format (CONTENT.md
/// «челлендж недели») with a window of <c>weeklyChallenge.durationDays</c> from <see cref="StartsAt"/>.
/// </summary>
public sealed record SetWeeklyChallenge(WeeklyChallengeDefinition Definition, DateTimeOffset StartsAt) : ICommand;

/// <summary>The admin marks <see cref="PlayerId"/> as having completed a manual challenge with the run <see cref="RunId"/>.</summary>
public sealed record MarkWeeklyChallenge(Guid ChallengeId, Guid PlayerId, Guid RunId) : ICommand;

/// <summary>A weekly challenge was set: its window and the coins it pays.</summary>
[EventType("weekly-challenge-set")]
public sealed record WeeklyChallengeSet(
    Guid ChallengeId, WeeklyChallengeDefinition Definition, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int RewardCoins) : IGameEvent;

/// <summary>
/// The player completed the challenge with the run; <see cref="Coins"/> — what it paid (0 for the frozen first finisher),
/// the coins follow as their own change.
/// </summary>
[EventType("weekly-challenge-completed")]
public sealed record WeeklyChallengeCompleted(Guid ChallengeId, Guid PlayerId, Guid RunId, int Coins) : IGameEvent;

/// <summary>
/// The run's proof was rejected: the completion is gone, <see cref="Coins"/> of what it paid are taken back (0 for the frozen
/// first finisher, whose coins do not change, D-578).
/// </summary>
[EventType("weekly-challenge-revoked")]
public sealed record WeeklyChallengeRevoked(Guid ChallengeId, Guid PlayerId, Guid RunId, int Coins) : IGameEvent;

/// <summary>A completion a player holds: the run it stands on and the coins it paid.</summary>
public sealed record WeeklyChallengeCompletion(Guid PlayerId, Guid RunId, int Coins);

/// <summary>A weekly challenge of the season with its window and the completions in force (a revoked one is gone).</summary>
public sealed record WeeklyChallengeState(
    Guid ChallengeId,
    WeeklyChallengeDefinition Definition,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int RewardCoins,
    EquatableArray<WeeklyChallengeCompletion> Completions)
{
    /// <summary>The window is <c>[StartsAt, EndsAt)</c> by the moment the run was completed (D-526, D-574).</summary>
    public bool InWindow(DateTimeOffset moment) => moment >= StartsAt && moment < EndsAt;

    public bool CompletedBy(Guid playerId) => Completions.Any(c => c.PlayerId == playerId);
}

/// <summary>
/// The weekly challenge (D-526): an automatic one is settled by a calculation after the command's events (D-413, not an
/// effect, so the chain limit never cuts it): a run completed in the window whose game and difficulty meet the condition
/// gives the player the reward once. A manual one is marked by the admin. A reject takes the completion and its coins
/// back; the frozen first finisher completes without coins and keeps his coins on a reject, as with a bet (D-414, D-578).
/// </summary>
internal static class WeeklyChallenges
{
    public static Decision Decide(SeasonState state, SetWeeklyChallenge command, EngineContext context)
    {
        if (SeasonSetup.RequireActive(state) is { } inactive)
        {
            return inactive;
        }

        if (!state.Rules.Features.WeeklyChallenge)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "The weekly challenge is off in this season.");
        }

        if (SeasonSetup.IsPastDeadline(state, context.Clock.UtcNow))
        {
            return Decision.Reject(RejectionCodes.SeasonDeadlinePassed, "The deadline has passed: no new challenges this season.");
        }

        ArgumentNullException.ThrowIfNull(command.Definition);
        if (ContentValidator.Check(command.Definition) is { Count: > 0 } errors)
        {
            return Decision.Reject(
                RejectionCodes.ContentInvalid, string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}")));
        }

        var rules = state.Rules.WeeklyChallenge;
        var days = rules.DurationDays ?? throw new InvalidOperationException("The validator requires weeklyChallenge.durationDays.");
        // The content format requires reward.coins (ContentValidator), so weeklyChallenge.defaultRewardCoins is not read (D-578)
        var reward = command.Definition.Reward.Coins ?? throw new InvalidOperationException("The content validator requires reward.coins.");

        // The window's end must be a moment of the calendar: a refusal, not an exception in the queue (D-578)
        var latest = DateTime.MaxValue.AddDays(-days);
        if (command.StartsAt.UtcDateTime > latest || command.StartsAt.DateTime > latest)
        {
            return Decision.Reject(RejectionCodes.WeeklyChallengeWindowInvalid, "The challenge's window would end past the calendar.");
        }

        return Decision.Accept(new WeeklyChallengeSet(
            context.Ids.NewId(), command.Definition, command.StartsAt, command.StartsAt.AddDays(days), reward));
    }

    public static Decision Decide(SeasonState state, MarkWeeklyChallenge command)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        // Runs completed before the deadline are still marked in closing; after the results nothing changes (D-516)
        if (SeasonSetup.IsOver(state))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: results are fixed.");
        }

        if (!state.Rules.Features.WeeklyChallenge)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "The weekly challenge is off in this season.");
        }

        if (state.WeeklyChallenges.GetValueOrDefault(command.ChallengeId) is not { } challenge)
        {
            return Decision.Reject(RejectionCodes.WeeklyChallengeUnknown, $"Challenge {command.ChallengeId} is not in the season.");
        }

        if (challenge.Definition.Check != ChallengeCheck.Manual)
        {
            return Decision.Reject(RejectionCodes.WeeklyChallengeNotManual, "An automatic challenge is checked by the completion itself.");
        }

        if (!state.Players.TryGetValue(command.PlayerId, out var player))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {command.PlayerId} is not in the season.");
        }

        if (!state.Runs.TryGetValue(command.RunId, out var run))
        {
            return Decision.Reject(RejectionCodes.RunUnknown, $"Run {command.RunId} is not in the season.");
        }

        if (run.PlayerId != player.PlayerId)
        {
            return Decision.Reject(RejectionCodes.NotYourRun, "The challenge is marked with a run of the player.");
        }

        if (run.Status != RunStatus.Completed)
        {
            return Decision.Reject(RejectionCodes.RunNotCompleted, $"The run is {run.Status}: only a completed run completes a challenge.");
        }

        if (!challenge.InWindow(run.CompletedAt!.Value))
        {
            return Decision.Reject(RejectionCodes.WeeklyChallengeOutsideWindow, "The run was not completed in the challenge's window.");
        }

        if (challenge.CompletedBy(player.PlayerId))
        {
            return Decision.Reject(RejectionCodes.WeeklyChallengeAlreadyCompleted, "The player has completed this challenge already.");
        }

        return Decision.Accept([.. Complete(challenge, player, run.RunId)]);
    }

    /// <summary>What <paramref name="e"/> (the state already holds it) settles: completions of automatic challenges, revocations.</summary>
    public static IEnumerable<IGameEvent> Settle(SeasonState state, Pool.IPoolView pool, IGameEvent e)
    {
        switch (e)
        {
            case RunCompleted completed:
                var run = state.Runs[completed.RunId];
                foreach (var challenge in state.WeeklyChallenges.Values.Where(c =>
                    c.Definition.Check == ChallengeCheck.Auto && c.InWindow(completed.CompletedAt)))
                {
                    // The coop partner completes it too, each with the whole reward (D-521, D-573): with task 5.10
                    var player = state.Players[run.PlayerId];
                    if (!challenge.CompletedBy(player.PlayerId)
                        && Effects.Conditions.Hold(state, pool, player.PlayerId, challenge.Definition.Condition, run.RunId))
                    {
                        foreach (var next in Complete(challenge, player, run.RunId))
                        {
                            yield return next;
                        }
                    }
                }

                break;
            case ProofRejected rejected:
                foreach (var challenge in state.WeeklyChallenges.Values)
                {
                    foreach (var completion in challenge.Completions.Where(c => c.RunId == rejected.RunId))
                    {
                        // The frozen first's coins do not change (D-99, Q-3, D-422 (2), D-578): the completion goes, the coins stay
                        var coins = Finishes.IsFrozen(state.Players[completion.PlayerId]) ? 0 : completion.Coins;
                        yield return new WeeklyChallengeRevoked(challenge.ChallengeId, completion.PlayerId, completion.RunId, coins);
                        if (coins != 0)
                        {
                            yield return new CoinsChanged(completion.PlayerId, -coins, CoinsReason.WeeklyChallengeRevoked, completion.RunId);
                        }
                    }
                }

                break;
            default:
                break;
        }
    }

    // The frozen first finisher completes without coins, as a bet is won without them (D-414, D-526)
    private static IEnumerable<IGameEvent> Complete(WeeklyChallengeState challenge, SeasonPlayer player, Guid runId)
    {
        var coins = Finishes.IsFrozen(player) ? 0 : challenge.RewardCoins;
        yield return new WeeklyChallengeCompleted(challenge.ChallengeId, player.PlayerId, runId, coins);
        if (coins != 0)
        {
            yield return new CoinsChanged(player.PlayerId, coins, CoinsReason.WeeklyChallenge, runId);
        }
    }

    /// <summary>
    /// Whether switching the mechanic off would strand a reward (D-420, D-574): a completion stands on a run whose proof is not
    /// approved yet, so a reject could no longer take it back.
    /// </summary>
    public static bool InPlay(SeasonState state) =>
        state.WeeklyChallenges.Values.SelectMany(c => c.Completions).Any(c =>
            state.Runs.TryGetValue(c.RunId, out var run) && run.Status == RunStatus.Completed && run.Proof?.Status is not ProofStatus.Approved);

    public static SeasonState Apply(SeasonState state, WeeklyChallengeSet e) =>
        state with
        {
            WeeklyChallenges = state.WeeklyChallenges.Add(
                e.ChallengeId, new WeeklyChallengeState(e.ChallengeId, e.Definition, e.StartsAt, e.EndsAt, e.RewardCoins, [])),
        };

    public static SeasonState Apply(SeasonState state, WeeklyChallengeCompleted e) =>
        Update(state, e.ChallengeId, c => c with { Completions = Ordered([.. c.Completions, new WeeklyChallengeCompletion(e.PlayerId, e.RunId, e.Coins)]) });

    /// <summary>
    /// Completions in one order whatever came first — by player, then run — so an undo that gives one player's completion
    /// back leaves the state the log without the undone command gives (D-578).
    /// </summary>
    public static EquatableArray<WeeklyChallengeCompletion> Ordered(IEnumerable<WeeklyChallengeCompletion> completions) =>
        [.. completions.OrderBy(c => c.PlayerId).ThenBy(c => c.RunId)];

    public static SeasonState Apply(SeasonState state, WeeklyChallengeRevoked e) =>
        Update(state, e.ChallengeId, c => c with
        {
            Completions = [.. c.Completions.Where(x => !(x.PlayerId == e.PlayerId && x.RunId == e.RunId))],
        });

    private static SeasonState Update(SeasonState state, Guid challengeId, Func<WeeklyChallengeState, WeeklyChallengeState> change) =>
        state with { WeeklyChallenges = state.WeeklyChallenges.SetItem(challengeId, change(state.WeeklyChallenges[challengeId])) };
}
