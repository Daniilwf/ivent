using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Runs;

internal static class RunLifecycle
{
    public static Decision Decide(SeasonState state, StartRun command, EngineContext context)
    {
        if (TurnRules.Check(state, command.PlayerId, command, context.Clock.UtcNow) is { } rejection)
        {
            return rejection;
        }

        var player = state.Players[command.PlayerId];
        var offer = player.Offer ?? throw new InvalidOperationException($"Player {player.PlayerId} is Rolling without an offer.");
        return Decision.Accept(new RunStarted(
            context.Ids.NewId(), player.PlayerId, offer.GameId, offer.Snapshot, offer.RolledAt, context.Clock.UtcNow));
    }

    public static Decision Decide(SeasonState state, CompleteRun command, EngineContext context)
    {
        if (TurnRules.Check(state, command.PlayerId, command, context.Clock.UtcNow) is { } rejection)
        {
            return rejection;
        }

        if (!Enum.IsDefined(command.Difficulty))
        {
            return Decision.Reject(RejectionCodes.CommandInvalid, $"Unknown difficulty {command.Difficulty}.");
        }

        if (command.ChallengeDone && !state.Rules.Features.Challenges)
        {
            // Games carry no challenge note yet and the proof does not check it: claims are off (D-96).
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Challenges are off in this season.");
        }

        var player = state.Players[command.PlayerId];
        var run = state.Runs[player.ActiveRunId ?? throw new InvalidOperationException($"Player {player.PlayerId} is Playing without a run.")];

        // Hours come from the snapshot; the player's estimate counts only when the pool had none (D-44),
        // and then it needs a source (D-96).
        var estimated = run.Snapshot.Hours is not > 0;
        var hours = estimated ? command.EstimatedHours : run.Snapshot.Hours;
        if (hours is null)
        {
            return Decision.Reject(RejectionCodes.HoursRequired, "The game has no hours: give an estimate with a source.");
        }

        if (hours <= 0)
        {
            return Decision.Reject(RejectionCodes.InvalidHours, $"Hours must be positive, got {hours}.");
        }

        var source = estimated ? command.HoursSource : null;
        if (estimated && string.IsNullOrWhiteSpace(source))
        {
            return Decision.Reject(RejectionCodes.HoursSourceRequired, "An hours estimate needs its source.");
        }

        if (source?.Length > Limits.MaxHoursSourceLength)
        {
            return Decision.Reject(RejectionCodes.HoursSourceTooLong, $"The source is limited to {Limits.MaxHoursSourceLength} characters.");
        }

        if (command.Review is { } review && ReviewProblem(review) is { } badReview)
        {
            // The completion is one command: a bad review rejects it as a whole (D-96).
            return badReview;
        }

        var now = context.Clock.UtcNow;
        var count = CompletionRoll.Count(hours.Value, run.Snapshot);
        var die = CompletionRoll.DieFor(command.Difficulty, run.Snapshot.DieByDifficulty);

        // Items and effects change the throw (D-408): what waits for it, then the beforeDice effects firing now.
        var pending = player.Wallet.NextDice.Count;
        var (fired, afterFiring, changes) = DicePipeline.BeforeDice(state, player.PlayerId, run.RunId, context);
        player = afterFiring.Players[player.PlayerId];
        var thrown = DicePipeline.Throw(count, die, command.ChallengeDone ? run.Snapshot.ChallengeExtraDice : 0, changes, context.Random);
        var dice = thrown.Dice;
        var challengeDice = thrown.ChallengeDice;
        var sum = CompletionRoll.Total(dice, challengeDice, run.Snapshot, thrown.Mods);

        var events = new List<IGameEvent>(fired)
        {
            // Whether the run comes after the finish or in the first's free mode is stored, not recomputed on replay (D-99).
            new RunCompleted(
                run.RunId,
                player.PlayerId,
                command.Difficulty,
                hours.Value,
                now,
                source,
                command.ChallengeDone,
                AfterFinish: player.Finish is not null,
                FreeMode: player.Finish is not null && FinishLine.First(state) == player.PlayerId),
            new CompletionRolled(run.RunId, player.PlayerId, dice, challengeDice),
        };
        if (thrown.Mods is { } mods)
        {
            events.Add(new RunDiceModified(run.RunId, player.PlayerId, mods, thrown.Rolled, SpentNext: pending));
        }

        // The frozen first plays in free mode: dice only (the freeze amendment, Q-3).
        if (Finishes.IsFrozen(player))
        {
            if (command.Review is { } kept)
            {
                events.Add(Reviewed(run, kept, now));
            }

            return Decision.Accept(events);
        }

        if (sum != 0)
        {
            events.Add(new PointsChanged(player.PlayerId, sum, PointsReason.CompletionRoll, run.RunId));
        }

        // A finisher's position is fixed (Q-3); otherwise the token moves and may reach the finish (D-99). The player's
        // own walk stops at a fork with steps left and waits for the branch (D-304).
        PlayerMoved? moved = null;
        var walk = player.Finish is null ? Movement.WalkOwn(state.Map, player.CellId, sum) : new Walk([], 0);
        if (walk.Path.Count > 0)
        {
            moved = new PlayerMoved(
                player.PlayerId, player.CellId, walk.Path[^1], sum, [.. walk.Path], MoveReason.CompletionRoll, run.RunId, walk.Paused);
            events.Add(moved);
        }

        // Coins by the counted hours, from the rules fixed at the roll (Q-2, D-96), up to the hours the dice top out at:
        // a player's estimate cannot mint coins without limit.
        if (run.Snapshot.Coins is { } reward)
        {
            var coins = CompletionCoins(reward, run.Snapshot.DiceCount, hours.Value);
            if (coins != 0)
            {
                events.Add(new CoinsChanged(player.PlayerId, coins, CoinsReason.CompletionReward, run.RunId));
            }
        }

        if (die.GrantEvent is { } granted)
        {
            events.Add(new ManualEffectCreated(context.Ids.NewId(), player.PlayerId, granted, ManualEffectSource.Difficulty, run.RunId));
        }

        // The finish comes after the run's own rewards, so a first frozen at once is frozen after them (D-99); then the
        // cell of the stop, or the branch choice of a paused walk (D-303, D-304).
        events.AddRange(AfterOwnMove(events.Aggregate(state, SeasonEngine.Apply), player, run, moved, walk, context));

        if (command.Review is { } given)
        {
            events.Add(Reviewed(run, given, now));
        }

        return Decision.Accept(events);
    }

    /// <summary>
    /// After the player's own move of a run (<paramref name="moved"/>, null when no cell was entered) with
    /// <paramref name="state"/> the state after it: the finish and its settlement, or else the stop on the cell; when the
    /// walk paused at a fork, the branch choice with the steps left (D-99, D-303, D-304).
    /// </summary>
    public static IEnumerable<IGameEvent> AfterOwnMove(
        SeasonState state, SeasonPlayer player, RunState run, PlayerMoved? moved, Walk walk, EngineContext context)
    {
        var events = new List<IGameEvent>();
        if (moved is not null && !walk.Paused)
        {
            var finished = Finishes.AfterCompletionMove(state, player, run, moved, context.Clock.UtcNow).ToList();
            // A move that reached the finish still passed its shops (D-403)
            events.AddRange(finished.Count > 0 ? [.. CellStops.ShopGrants(state, moved, context), .. finished] : CellStops.After(state, moved, context));
        }

        if (walk.Paused)
        {
            // The shop cells passed on the way to the fork grant their coupon (D-403)
            if (moved is not null)
            {
                events.AddRange(CellStops.After(state, moved, context));
            }

            var fork = moved?.To ?? player.CellId;
            events.Add(new BranchChoiceRequested(
                player.PlayerId, context.Ids.NewId(), fork, [.. state.Map.Exits(fork).Select(e => e.To)], walk.Remaining,
                MoveReason.CompletionRoll, run.RunId));
        }

        return events;
    }

    /// <summary>Coins for completing (Q-2, D-96): by the hours, up to the hours the dice top out at, at least the minimum.</summary>
    public static int CompletionCoins(Rulesets.CoinReward reward, Rulesets.DiceCountRule dice, decimal hours) =>
        Math.Max(reward.Min, (int)Math.Floor(Math.Min(hours, dice.Max * dice.HoursPerDie) * reward.PerHour));

    public static Decision Decide(SeasonState state, ReviewRun command, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "The season does not exist yet.");
        }

        if (state.Status == SeasonStatus.Archived)
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, "The season is archived.");
        }

        if (!state.Players.ContainsKey(command.PlayerId))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {command.PlayerId} is not in the season.");
        }

        if (!state.Runs.TryGetValue(command.RunId, out var run))
        {
            return Decision.Reject(RejectionCodes.RunUnknown, $"Run {command.RunId} is not in the season.");
        }

        if (run.PlayerId != command.PlayerId)
        {
            return Decision.Reject(RejectionCodes.NotYourRun, "Only the player who completed a run reviews it.");
        }

        if (run.Status != RunStatus.Completed)
        {
            return Decision.Reject(RejectionCodes.RunNotCompleted, $"Run {run.RunId} is {run.Status}.");
        }

        return ReviewProblem(command.Review) ?? Decision.Accept(Reviewed(run, command.Review, context.Clock.UtcNow));
    }

    public static SeasonState Apply(SeasonState state, RunReviewed e) =>
        state with { Runs = state.Runs.SetItem(e.RunId, state.Runs[e.RunId] with { Review = new RunReview(e.Rating, e.Text) }) };

    private static Decision? ReviewProblem(RunReview review) =>
        review.Rating is < 1 or > 10
            ? Decision.Reject(RejectionCodes.InvalidRating, $"A rating is 1–10, got {review.Rating}.")
            : review.Text?.Length > Limits.MaxReviewLength
                ? Decision.Reject(RejectionCodes.ReviewTooLong, $"A review is limited to {Limits.MaxReviewLength} characters.")
                : null;

    // The text is trimmed; blank text is no text.
    private static RunReviewed Reviewed(RunState run, RunReview review, DateTimeOffset at) =>
        new(run.RunId, run.PlayerId, review.Rating, string.IsNullOrWhiteSpace(review.Text) ? null : review.Text.Trim(), at);

    public static SeasonState Apply(SeasonState state, RunStarted e)
    {
        var run = new RunState(
            e.RunId, e.PlayerId, e.GameId, RunStatus.Playing, e.Snapshot, e.RolledAt, e.StartedAt, Difficulty: null, Hours: null, Dice: []);
        var player = state.Players[e.PlayerId] with { Phase = TurnPhase.Playing, Offer = null, RerollsThisRoll = 0, ActiveRunId = e.RunId };
        state = state with { Runs = state.Runs.Add(e.RunId, run), Players = state.Players.SetItem(e.PlayerId, player) };

        // The changes of the roll hold until the game starts (D-405).
        return player.Wallet.CurrentRoll.Count > 0 ? Inventory.Inventories.Update(state, e.PlayerId, w => w with { CurrentRoll = [] }) : state;
    }

    public static SeasonState Apply(SeasonState state, RunCompleted e)
    {
        var run = state.Runs[e.RunId] with
        {
            Status = RunStatus.Completed,
            Difficulty = e.Difficulty,
            Hours = e.Hours,
            HoursSource = e.HoursSource,
            CompletedAt = e.CompletedAt,
            AfterFinish = e.AfterFinish,
            FreeMode = e.FreeMode,
        };
        var player = state.Players[e.PlayerId] with { Phase = TurnPhase.Idle, ActiveRunId = null };
        return state with { Runs = state.Runs.SetItem(e.RunId, run), Players = state.Players.SetItem(e.PlayerId, player) };
    }

    public static SeasonState Apply(SeasonState state, CompletionRolled e) =>
        state with { Runs = state.Runs.SetItem(e.RunId, state.Runs[e.RunId] with { Dice = e.Dice, ChallengeDice = e.ChallengeDice }) };
}
