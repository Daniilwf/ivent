using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using GameEvent.Engine.Content;
using GameEvent.Engine.Gallery;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Polls;

/// <summary>The admin starts a final ballot over approved public candidates (D-927).</summary>
public sealed record StartNominationPoll(Guid PollId, VotedNomination Category, EquatableArray<Guid> CandidateIds) : ICommand;

/// <summary>The pool removed a game; the queue forwards the change to seasons with affected nominations.</summary>
public sealed record CancelNominationsForDeletedGame(Guid GameId) : ICommand;

/// <summary>A ballot's public candidate references and voting rules at launch. Names and media resolve from current visibility.</summary>
[EventType("nomination-poll-started")]
public sealed record NominationPollStarted(Guid PollId, VotedNomination Category, EquatableArray<Guid> CandidateIds,
    PollVoters Voters, bool Anonymous, bool ShowLiveCounts, DateTimeOffset StartedAt, DateTimeOffset ClosesAt) : IGameEvent
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EquatableArray<NominationReviewVersion>? ReviewVersions { get; init; }
}

/// <summary>The exact saved version of a text review. Only the fingerprint belongs to nomination history.</summary>
public sealed record NominationReviewVersion(Guid CandidateId, string TextHash)
{
    internal static NominationReviewVersion Capture(Guid candidateId, string text) => new(candidateId, Hash(candidateId, text));

    internal bool Matches(string text) => string.Equals(TextHash, Hash(CandidateId, text), StringComparison.Ordinal);

    private static string Hash(Guid candidateId, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{candidateId:D}\n{text.Trim()}")));
}

internal static class NominationPolling
{
    public static Decision Decide(SeasonState state, StartNominationPoll command, EngineContext context)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (!state.Rules.Features.SeasonNominations || !state.Rules.Features.Polls
            || !state.Rules.Nominations.Voted.Contains(command.Category))
        {
            return Decision.Reject(RejectionCodes.FeatureDisabled, "This nomination is off in this season.");
        }

        if (state.Status != SeasonStatus.Closing)
        {
            return Decision.Reject(RejectionCodes.NominationNeedsClosing, "A final ballot starts while the season is closing.");
        }

        if (SeasonSetup.ProofsPending(state) is { } pending)
        {
            return pending;
        }

        if (command.PollId == Guid.Empty || state.Polls.ContainsKey(command.PollId)
            || InPlay(state).Any(p => p.Nomination == command.Category))
        {
            return Decision.Reject(RejectionCodes.NominationAlreadyStarted, "There is already a ballot for this nomination, or the poll ID is unavailable.");
        }

        if (state.Rules.Nominations is not { DurationHours: { } duration, MinCandidates: { } minimum, MaxCandidates: { } maximum }
            || command.CandidateIds.Count < minimum || command.CandidateIds.Count > maximum
            || command.CandidateIds.Distinct().Count() != command.CandidateIds.Count)
        {
            return Decision.Reject(RejectionCodes.NominationCandidatesInvalid, "Select distinct candidates within this season's nomination limits.");
        }

        if (context.AvailableNominationCandidates is not { } available ||
            command.CandidateIds.Any(id => id == Guid.Empty || !available.Contains(id)
                || !AvailableInSeason(state, command.Category, id)))
        {
            return Decision.Reject(RejectionCodes.NominationCandidatesInvalid, "Select only public, eligible candidates from this season.");
        }

        var now = context.Clock.UtcNow;
        return Decision.Accept(new NominationPollStarted(command.PollId, command.Category, command.CandidateIds,
            PollVoters.Everyone, Anonymous: false, Polling.LiveCounts(state, anonymous: false, asked: null), now, now.AddHours(duration))
        {
            ReviewVersions = command.Category == VotedNomination.BestReview
                ? [.. command.CandidateIds.Select(id => NominationReviewVersion.Capture(id, state.Runs[id].Review!.Text!))]
                : null,
        });
    }

    public static Decision Decide(SeasonState state, CancelNominationsForDeletedGame command)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "The season does not exist.");
        }

        // A published result keeps its winner reference. The public read resolves the current candidate separately.
        var affected = state.Status == SeasonStatus.Closing
            ? InPlay(state).Where(p => p.Options.Any(o => ReferencesGame(state, p.Nomination, Guid.Parse(o.Id), command.GameId)))
            : [];
        return Decision.Accept([.. Cancel(affected)]);
    }

    /// <summary>Any candidate that ceased to qualify before publication cancels its whole ballot.</summary>
    public static IEnumerable<PollCancelled> After(SeasonState state, IReadOnlyList<IGameEvent> events)
    {
        if (state.Status != SeasonStatus.Closing || !events.Any(MayHideCandidate))
        {
            return [];
        }

        return Cancel(InPlay(state).Where(p => p.Nomination is { } category
            && p.Options.Any(o => !AvailableInSeason(state, category, Guid.Parse(o.Id), p))));
    }

    public static IEnumerable<Poll> InPlay(SeasonState state) =>
        state.Polls.Values.Where(p => p.Source == PollSource.Nomination && p.Status != PollStatus.Cancelled);

    public static SeasonState Apply(SeasonState state, NominationPollStarted e) =>
        state with
        {
            Polls = state.Polls.SetItem(e.PollId, new Poll(
                e.PollId, PollSource.Nomination, "", [.. e.CandidateIds.Select(id => new PollOption(id.ToString("D"), ""))],
                e.Voters, e.Anonymous, e.ShowLiveCounts, e.StartedAt, e.ClosesAt, PollStatus.Open, [])
            {
                Nomination = e.Category,
                ReviewVersions = e.ReviewVersions,
            }),
        };

    private static IEnumerable<PollCancelled> Cancel(IEnumerable<Poll> polls) =>
        polls.OrderBy(p => p.PollId).Select(p => new PollCancelled(p.PollId));

    private static bool MayHideCandidate(IGameEvent e) =>
        e is RunReviewed or ProofSubmitted or ProofRejected or GalleryMomentHidden or GalleryProofConsentWithdrawn
            or CommandUndone or RulesetChanged;

    private static bool AvailableInSeason(SeasonState state, VotedNomination category, Guid id, Poll? ballot = null) => category switch
    {
        VotedNomination.BestReview => state.Runs.GetValueOrDefault(id) is { } run && Credited(run)
            && (ballot is null ? !string.IsNullOrWhiteSpace(run.Review?.Text) : ballot.MatchesNominationReview(id, run.Review?.Text)),
        VotedNomination.GameOfSeason => state.Runs.Values.Any(run => run.GameId == id && Credited(run)),
        VotedNomination.FunniestMoment => state.Rules.Features.Gallery && state.GalleryItems.GetValueOrDefault(id) is { Visible: true },
        _ => false,
    };

    private static bool Credited(RunState run) => run is { Status: RunStatus.Completed, Proof.Status: ProofStatus.Approved };

    private static bool ReferencesGame(SeasonState state, VotedNomination? category, Guid candidateId, Guid gameId) =>
        category switch
        {
            VotedNomination.GameOfSeason => candidateId == gameId,
            VotedNomination.BestReview => state.Runs.GetValueOrDefault(candidateId)?.GameId == gameId,
            _ => false,
        };
}
