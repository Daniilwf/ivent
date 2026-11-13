using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Polls;

/// <summary>
/// Polls (SPEC «Голосования», D-512…D-516, D-541, D-720…): the admin or a drawn event starts one with a snapshot of its
/// options; users vote, one vote each, the last one counts; the timer or the admin closes it, the most votes win, a tie is
/// broken by a recorded roll, no votes — no result. The result's effect runs in the closing command, its targets counted
/// then; on everyone it is settled by <see cref="ForAllSettlements"/> (D-537). The deadline cancels a poll whose result acts.
/// </summary>
internal static class Polling
{
    /// <summary>The label of the effect of a poll's result in its events (an object id of no content).</summary>
    public const string ResultObjectId = "poll-result";

    /// <summary>The parameter <c>$result</c> of the result's effect.</summary>
    public const string ResultParameter = "result";

    public static Decision Decide(SeasonState state, StartPoll command, EngineContext context)
    {
        ArgumentNullException.ThrowIfNull(command.Definition);
        if (SeasonSetup.RequireActive(state) is { } inactive)
        {
            return inactive;
        }

        if (!state.Rules.Features.Polls)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "Polls are off in this season.");
        }

        var definition = command.Definition;

        // A result that acts lives in the mechanic of items (D-544)
        if (definition.OnResult is not null && !state.Rules.Features.Items)
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "A poll whose result acts needs items on (D-544).");
        }

        if (SeasonSetup.IsPastDeadline(state, context.Clock.UtcNow))
        {
            return Decision.Reject(RejectionCodes.SeasonDeadlinePassed, "The deadline has passed: no new polls this season.");
        }

        var errors = ContentValidator.Check(definition).ToList();
        if (definition.OnResult is { } result && result.Target?.Selector is not (TargetSelector.Leader or TargetSelector.RandomActive or TargetSelector.All))
        {
            errors.Add(new ContentError("$.onResult.target.selector", "A poll's result acts on «leader», «randomActive» or «all»: nobody is its user."));
        }

        if (errors.Count > 0)
        {
            return Decision.Reject(RejectionCodes.ContentInvalid, string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}")));
        }

        // The games are offered to whom the result will act on, as the leader stands now (D-512)
        var target = definition.OnResult?.Target?.Selector == TargetSelector.Leader ? Leader(state, definition.OnResult) : null;
        var options = Options(state, context, definition.Options, target);
        if (options.Count < 2)
        {
            return Decision.Reject(RejectionCodes.PollNoOptions, $"The poll would offer {options.Count} option(s): it needs two at least.");
        }

        var now = context.Clock.UtcNow;
        return Decision.Accept(new PollStarted(
            context.Ids.NewId(),
            PollSource.Admin,
            definition.Question.Trim(),
            options,
            definition.Voters,
            definition.Anonymous,
            LiveCounts(state, definition.Anonymous, definition.ShowLiveCounts),
            now,
            now.AddHours(definition.ClosesInHours))
        {
            OnResult = definition.OnResult,
            TargetId = definition.Options.From == ChoiceSource.Games ? target : null,
        });
    }

    public static Decision Decide(SeasonState state, CastVote command, EngineContext context)
    {
        if (Guard(state) is { } refused)
        {
            return refused;
        }

        if (state.Polls.GetValueOrDefault(command.PollId) is not { } poll)
        {
            return Decision.Reject(RejectionCodes.PollUnknown, $"Poll {command.PollId} is not in the season.");
        }

        // The time is up by the engine's clock, before the timer closes it (D-513)
        if (poll.Status != PollStatus.Open || context.Clock.UtcNow >= poll.ClosesAt)
        {
            return Decision.Reject(RejectionCodes.PollClosed, "The poll is closed.");
        }

        // A user without a player of the season is a spectator (D-513)
        var player = state.Players.Values.FirstOrDefault(p => p.UserId == command.UserId);
        if (player is null && !(poll.Voters == PollVoters.Everyone && state.Rules.Social.SpectatorsCanVote))
        {
            return Decision.Reject(RejectionCodes.PollNotVoter, "Only the players of the season vote in this poll.");
        }

        // The first finisher no longer affects the others (SPEC, D-513): the provisional one too (Н-2)
        if (player is not null && poll.HasEffect && Targets.IsFirst(state, player.PlayerId))
        {
            return Decision.Reject(RejectionCodes.PollFrozenVoter, "The first finisher does not vote where the result acts on players.");
        }

        if (poll.Options.All(o => !string.Equals(o.Id, command.OptionId, StringComparison.Ordinal)))
        {
            return Decision.Reject(RejectionCodes.PollUnknownOption, "The option is not one of the poll's.");
        }

        if (poll.Votes.FirstOrDefault(v => v.UserId == command.UserId) is not { } vote)
        {
            return Decision.Accept(new VoteCast(poll.PollId, command.UserId, command.OptionId, poll.Anonymous, poll.ShowLiveCounts));
        }

        // A second tab sends the same vote again: nothing changes (Н-22)
        if (string.Equals(vote.OptionId, command.OptionId, StringComparison.Ordinal))
        {
            return Decision.Reject(RejectionCodes.PollSameVote, "This is the vote already cast.");
        }

        return state.Rules.Polls?.AllowChangeVote == true
            ? Decision.Accept(new VoteChanged(poll.PollId, command.UserId, command.OptionId, poll.Anonymous, poll.ShowLiveCounts))
            : Decision.Reject(RejectionCodes.PollAlreadyVoted, "A vote cannot be changed in this season.");
    }

    public static Decision Decide(SeasonState state, ClosePoll command, EngineContext context)
    {
        if (Guard(state) is { } refused)
        {
            return refused;
        }

        if (state.Polls.GetValueOrDefault(command.PollId) is not { } poll)
        {
            return Decision.Reject(RejectionCodes.PollUnknown, $"Poll {command.PollId} is not in the season.");
        }

        if (poll.Status != PollStatus.Open)
        {
            return Decision.Reject(RejectionCodes.PollClosed, "The poll is closed already.");
        }

        return Decision.Accept(Close(state, context, poll, PollCloseReason.Admin, eventsBefore: 0));
    }

    // The season runs or closes (a poll without an effect waits for its time after the deadline, D-516), polls are on (D-420)
    private static Decision? Guard(SeasonState state)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (state.Status is not (SeasonStatus.Active or SeasonStatus.Closing))
        {
            return Decision.Reject(RejectionCodes.SeasonClosed, $"The season is {state.Status}: polls are over.");
        }

        return state.Rules.Features.Polls ? null : Decision.Reject(RejectionCodes.FeatureDisabled, "Polls are off in this season.");
    }

    /// <summary>The timer closes the polls whose time has come, oldest first; nothing while polls are off (D-420).</summary>
    public static List<IGameEvent> Due(SeasonState state, EngineContext context, int eventsBefore)
    {
        var events = new List<IGameEvent>();
        if (!state.Rules.Features.Polls)
        {
            return events;
        }

        var now = context.Clock.UtcNow;
        foreach (var poll in state.Polls.Values.Where(p => p.Status == PollStatus.Open && p.ClosesAt <= now).OrderBy(p => p.ClosesAt).ThenBy(p => p.PollId).ToList())
        {
            var closed = Close(state, context, state.Polls[poll.PollId], PollCloseReason.Timer, eventsBefore + events.Count);
            events.AddRange(closed);
            state = closed.Aggregate(state, SeasonEngine.Apply);
        }

        return events;
    }

    /// <summary>When the next open poll closes (the scheduler's moment); null when none, or while polls are off (D-420).</summary>
    public static DateTimeOffset? NextClose(SeasonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Ruleset is { Features.Polls: true } && state.Polls.Values.Where(p => p.Status == PollStatus.Open).Select(p => (DateTimeOffset?)p.ClosesAt).Min() is { } next
            ? next
            : null;
    }

    /// <summary>
    /// At the deadline the polls whose result acts are cancelled (D-516): the results of the season must not change after it.
    /// The others wait for their time.
    /// </summary>
    public static IEnumerable<IGameEvent> CancelAtDeadline(SeasonState state) =>
        state.Polls.Values.Where(p => p.Status == PollStatus.Open && p.HasEffect).Select(p => new PollCancelled(p.PollId));

    /// <summary>
    /// The close (D-514): counts, the result — most votes, a tie broken by a recorded roll, none without votes — and the
    /// result's effect in the same command, its targets counted now. After the deadline a poll whose result acts is cancelled
    /// instead (D-516).
    /// </summary>
    private static List<IGameEvent> Close(SeasonState state, EngineContext context, Poll poll, PollCloseReason reason, int eventsBefore)
    {
        if (poll.HasEffect && (state.Status != SeasonStatus.Active || SeasonSetup.IsPastDeadline(state, context.Clock.UtcNow)))
        {
            return [new PollCancelled(poll.PollId)];
        }

        var counts = poll.Counts();
        var most = counts.Max(c => c.Votes);
        EquatableArray<string> tied = most == 0 ? [] : [.. counts.Where(c => c.Votes == most).Select(c => c.OptionId)];
        int? roll = tied.Count > 1 ? context.Random.NextInt(0, tied.Count) : null;
        var result = tied.Count == 0 ? null : tied[roll ?? 0];
        var closed = new PollClosed(poll.PollId, reason, result, counts, tied.Count > 1 ? tied : [], roll);
        List<IGameEvent> events = [closed];
        if (result is null)
        {
            return events;
        }

        var after = SeasonEngine.Apply(state, closed);
        if (poll.Source == PollSource.Event)
        {
            // The event that asked goes on with the result as its answer (D-512)
            if (poll.PlayerId is { } drawer && poll.ChoiceId is { } choice
                && state.Players.GetValueOrDefault(drawer)?.Suspended.FirstOrDefault(s => s.ChoiceId == choice) is { } suspended)
            {
                events.AddRange(Suspensions.Continue(after, context, drawer, suspended, result));
            }

            return events;
        }

        if (poll.OnResult is not { } effect)
        {
            return events;
        }

        var parameters = new ContentParamDictionary([new(ResultParameter, result)]);
        if (effect.Target?.Selector == TargetSelector.All)
        {
            // On everyone in a settlement of its own: the close only queues it (D-537)
            events.Add(ForAllSettlements.Queue(after, context, ForAllSource.PollResult, poll.PollId, ResultObjectId, effect, parameters, poll.Kind));
            return events;
        }

        var target = effect.Target?.Selector == TargetSelector.RandomActive ? RandomActive(after, context, effect) : Leader(after, effect);
        if (target is not { } id)
        {
            return events;
        }

        var run = new EffectRun(after, context, id, ResultObjectId, hostile: false, parameters, eventsBefore: eventsBefore + events.Count)
        {
            PositionLocked = SeasonSetup.IsPastDeadline(after, context.Clock.UtcNow),
            EventKind = poll.Kind,
        };
        run.Run(effect, [id]);
        if (run.Refusal is null)
        {
            events.AddRange(run.Events);
        }

        return events;
    }

    // Whom a result may reach (SPEC «Цели»): not the first finisher; one who finished not first only by points and coins
    private static IEnumerable<SeasonPlayer> Reachable(SeasonState state, EffectSpec? effect) =>
        state.Players.Values.Where(p => !Targets.IsFirst(state, p.PlayerId) && (p.Finish is null || Targets.PointsAndCoinsOnly(effect)));

    /// <summary>The leader the result reaches (D-514): the most points among the reachable, as the selector «leader» picks.</summary>
    private static Guid? Leader(SeasonState state, EffectSpec? effect) =>
        Reachable(state, effect).OrderByDescending(p => p.Points).ThenBy(p => p.PointsTick).ThenBy(p => p.PlayerId)
            .Select(p => (Guid?)p.PlayerId).FirstOrDefault();

    private static Guid? RandomActive(SeasonState state, EngineContext context, EffectSpec effect)
    {
        var active = Reachable(state, effect).Where(p => !p.IsInactive).ToList();
        return active.Count == 0 ? null : active[context.Random.NextInt(0, active.Count)].PlayerId;
    }

    /// <summary>Whether a poll shows its counts before the close (D-541): never an anonymous one.</summary>
    public static bool LiveCounts(SeasonState state, bool anonymous, bool? asked) =>
        !anonymous && (asked ?? state.Rules.Polls?.ShowLiveCountsDefault ?? false);

    /// <summary>
    /// The snapshot of a poll's options (D-512): the categories with an available game; the active players but the first
    /// finisher; a random sample of <c>polls.gameOptions</c> of the games available to <paramref name="target"/> (to anyone
    /// without one); the list as it is.
    /// </summary>
    public static EquatableArray<PollOption> Options(SeasonState state, EngineContext context, ChoiceOptionsSpec spec, Guid? target)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var status = target is { } id ? SeasonGameStatus.For(state, id) : SeasonGameStatus.ForNobody(state);
        var pool = context.Pool;
        var categories = pool.Categories.Where(c => c.Weight > 0).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        var available = pool.Games
            .Where(g => status.Of(g) == GameAvailability.Available && categories.Any(c => Rolling.InCategory(g, c)))
            .OrderBy(g => g.Id)
            .ToList();
        switch (spec.From)
        {
            case ChoiceSource.Categories:
                return [.. categories.Where(c => available.Any(g => Rolling.InCategory(g, c))).Select(c => new PollOption(c.Name, c.Name))];
            case ChoiceSource.Players:
                return [.. state.Players.Values
                    .Where(p => !p.IsInactive && !Targets.IsFirst(state, p.PlayerId))
                    .Select(p => new PollOption(p.PlayerId.ToString("N"), p.Name))];
            case ChoiceSource.Games:
                return [.. Sample(available, state.Rules.Polls?.GameOptions ?? 0, context.Random).Select(g => new PollOption(g.Id.ToString("N"), g.Title))];
            default:
                return [.. (spec.List ?? []).Select(text => new PollOption(text, text))];
        }
    }

    // A random sample without replacement, in the order drawn (written in the event, so a replay never draws again, EV49)
    private static List<Game> Sample(List<Game> games, int count, IRandomSource random)
    {
        var left = games.ToList();
        var taken = new List<Game>();
        while (taken.Count < count && left.Count > 0)
        {
            var index = random.NextInt(0, left.Count);
            taken.Add(left[index]);
            left.RemoveAt(index);
        }

        return taken;
    }

    public static SeasonState Apply(SeasonState state, PollStarted e) =>
        state with
        {
            Polls = state.Polls.SetItem(
                e.PollId,
                new Poll(e.PollId, e.Source, e.Question, e.Options, e.Voters, e.Anonymous, e.ShowLiveCounts, e.StartedAt, e.ClosesAt, PollStatus.Open, [])
                {
                    OnResult = e.OnResult,
                    PlayerId = e.PlayerId,
                    ChoiceId = e.ChoiceId,
                    ObjectId = e.ObjectId,
                    Kind = e.Kind,
                    TargetId = e.TargetId,
                }),
        };

    public static SeasonState Apply(SeasonState state, VoteCast e) => Voted(state, e.PollId, e.UserId, e.OptionId);

    public static SeasonState Apply(SeasonState state, VoteChanged e) => Voted(state, e.PollId, e.UserId, e.OptionId);

    private static SeasonState Voted(SeasonState state, Guid pollId, Guid userId, string optionId)
    {
        var poll = state.Polls[pollId];
        return state with
        {
            Polls = state.Polls.SetItem(pollId, poll with { Votes = Ordered(poll.Votes.Where(v => v.UserId != userId).Append(new Vote(userId, optionId))) }),
        };
    }

    /// <summary>The votes in a canonical order (by user), so the state compares equal whatever order they came in or came back.</summary>
    public static EquatableArray<Vote> Ordered(IEnumerable<Vote> votes) => [.. votes.OrderBy(v => v.UserId)];

    // The event that waited is closed by the poll's end (D-512, D-516): its continuation, if any, follows as its own events
    public static SeasonState Apply(SeasonState state, PollClosed e)
    {
        var poll = state.Polls[e.PollId];
        state = state with { Polls = state.Polls.SetItem(e.PollId, poll with { Status = PollStatus.Closed, Result = e.Result }) };
        return e.Result is null ? Release(state, poll) : state;
    }

    public static SeasonState Apply(SeasonState state, PollCancelled e)
    {
        var poll = state.Polls[e.PollId];
        return Release(state with { Polls = state.Polls.SetItem(e.PollId, poll with { Status = PollStatus.Cancelled }) }, poll);
    }

    // The suspended effect of an event's poll ends without its actions (D-514: no result, no effect)
    private static SeasonState Release(SeasonState state, Poll poll) =>
        poll is { PlayerId: { } drawer, ChoiceId: { } choice } && Suspensions.IsSuspended(state, drawer, choice)
            ? Suspensions.Close(state, drawer, choice)
            : state;
}
