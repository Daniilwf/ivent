using System.Numerics;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Statistics;

public sealed record AutomaticNominationResult(AutoNomination Nomination, EquatableArray<Guid> WinnerPlayerIds, decimal Value);

public sealed record VotedNominationResult(VotedNomination Nomination, Guid PollId, Guid? WinnerCandidateId);

/// <summary>The results of the season's nominations, frozen along with its final table (D-927).
/// Voted nominees are stored by reference so current moderation still controls public access to their content.</summary>
[EventType("season-nominations-recorded")]
public sealed record SeasonNominationsRecorded(
    EquatableArray<AutomaticNominationResult> Automatic,
    EquatableArray<VotedNominationResult> Voted) : IGameEvent;

internal static class SeasonNominations
{
    public static SeasonNominationsRecorded Record(SeasonState state, EngineContext context)
    {
        var automatic = state.Rules.Nominations.Auto.Distinct().Select(nomination => nomination switch
        {
            AutoNomination.MostHours => MostHours(state),
            AutoNomination.MostRuns => Highest(nomination,
                CompletionStatistics.Build(state).Players.Select(player => (player.PlayerId, (decimal)player.CompletedRuns))),
            AutoNomination.UnluckiestDice => Unluckiest(state),
            AutoNomination.MostDrops => Highest(nomination,
                DropStatistics.Build(state).Players.Select(player => (player.PlayerId, (decimal)player.DroppedRuns))),
            AutoNomination.MostAttacks => Highest(nomination,
                AttackStatistics.Build(state, EffectiveAttacks(context)).Players.Select(player => (player.PlayerId, (decimal)player.AttacksMade))),
            _ => throw new InvalidOperationException($"Unknown automatic nomination {nomination}."),
        }).ToEquatableArray();

        var voted = state.Polls.Values
            .Where(poll => poll is { Source: PollSource.Nomination, Status: PollStatus.Closed, Nomination: not null })
            .OrderBy(poll => poll.Nomination)
            .Select(poll => new VotedNominationResult(poll.Nomination!.Value, poll.PollId,
                Guid.TryParseExact(poll.Result, "D", out var candidateId) ? candidateId : null))
            .ToEquatableArray();

        return new SeasonNominationsRecorded(automatic, voted);
    }

    private static AutomaticNominationResult MostHours(SeasonState state) =>
        Highest(AutoNomination.MostHours, state.Runs.Values
            .Where(run => run.Status == RunStatus.Completed && run.Hours > 0)
            .SelectMany(run => RunShares.Players(run).Select(playerId => (PlayerId: playerId, Hours: run.Hours!.Value)))
            .GroupBy(entry => entry.PlayerId)
            .Select(group => (group.Key, group.Sum(entry => entry.Hours))));

    private static AutomaticNominationResult Highest(AutoNomination nomination, IEnumerable<(Guid PlayerId, decimal Value)> candidates)
    {
        var maximum = 0m;
        var winners = new List<Guid>();
        foreach (var (playerId, value) in candidates)
        {
            if (value <= 0 || value < maximum)
            {
                continue;
            }

            if (value > maximum)
            {
                maximum = value;
                winners.Clear();
            }

            winners.Add(playerId);
        }

        return new AutomaticNominationResult(nomination, winners.Order().ToEquatableArray(), maximum);
    }

    private static AutomaticNominationResult Unluckiest(SeasonState state)
    {
        var byPlayer = new Dictionary<Guid, LuckMean>();
        foreach (var run in state.Runs.Values.Where(run => run.Status == RunStatus.Completed))
        {
            // A coop throw belongs to the roller. A fully fixed throw has no relative luck (D-931).
            if (RollStatistics.Measure(run.Dice, run.ChallengeDice, run.Mods) is { HasLuck: true } roll)
            {
                byPlayer[run.PlayerId] = byPlayer.GetValueOrDefault(run.PlayerId, LuckMean.Empty).Add(roll);
            }
        }

        LuckMean? best = null;
        var winners = new List<Guid>();
        foreach (var (playerId, mean) in byPlayer)
        {
            var comparison = best is { } previous ? mean.CompareTo(previous) : -1;
            if (comparison > 0)
            {
                continue;
            }

            if (comparison < 0)
            {
                best = mean;
                winners.Clear();
            }

            winners.Add(playerId);
        }

        return new AutomaticNominationResult(AutoNomination.UnluckiestDice, winners.Order().ToEquatableArray(), best?.ToDecimal() ?? 0m);
    }

    private static IEnumerable<HostileReceived> EffectiveAttacks(EngineContext context)
    {
        if (context.EffectiveAttacks is { } attacks)
        {
            return attacks;
        }

        if (context.History is not { } history)
        {
            return [];
        }

        var undone = Undoing.UndoneCommands(history);
        return history.Where(command => !undone.Contains(command.CommandId)).SelectMany(command => command.Events).OfType<HostileReceived>();
    }

    private readonly record struct LuckMean(BigInteger SumNumerator, BigInteger SumDenominator, int Count)
    {
        private const decimal DecimalScale = 10_000_000_000_000_000_000_000_000_000m;

        public static LuckMean Empty => new(BigInteger.Zero, BigInteger.One, 0);

        public LuckMean Add(RollMeasurement roll)
        {
            var numerator = (SumNumerator * roll.Denominator) + (SumDenominator * roll.Numerator);
            var denominator = SumDenominator * roll.Denominator;
            var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
            return new LuckMean(numerator / divisor, denominator / divisor, Count + 1);
        }

        public int CompareTo(LuckMean other) =>
            (SumNumerator * other.SumDenominator * other.Count)
                .CompareTo(other.SumNumerator * SumDenominator * Count);

        public decimal ToDecimal()
        {
            // Decimal is for display and persistence. Rank by the exact fractions, then round the winner's mean once.
            var denominator = SumDenominator * Count;
            var scaled = BigInteger.DivRem(SumNumerator * (BigInteger)DecimalScale, denominator, out var remainder);
            var halfway = (remainder * 2).CompareTo(denominator);
            if (halfway > 0 || (halfway == 0 && !scaled.IsEven))
            {
                scaled++;
            }

            return (decimal)scaled / DecimalScale;
        }
    }
}
