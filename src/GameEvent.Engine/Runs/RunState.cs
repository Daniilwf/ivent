using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Runs;

public enum RunStatus
{
    Playing,
    Completed,
    Dropped,
    TechRerolled,

    /// <summary>Completed, then rejected by the admin: the game counts as not completed again (D-15).</summary>
    Rejected,
}

public enum Difficulty
{
    Easy,
    Normal,
    Hard,
    Extreme,
}

/// <summary>One die of a roll, stored separately so hour corrections can add or remove dice.</summary>
public sealed record Die(int Sides, int Value);

/// <summary>
/// Rules fixed for a run at roll time (SPEC «Снапшот»). <see cref="Hours"/> is the game length from the pool;
/// null means the player must give an estimate when completing. <see cref="TechRerollWindowHours"/> is the tech reroll
/// window after the roll (D-94); <see cref="ChallengeExtraDice"/> and <see cref="Coins"/> are the challenge bonus and
/// the coin reward for completing (D-96).
/// </summary>
public sealed record RunSnapshot(
    int RulesetVersion,
    decimal? Hours,
    DiceCountRule DiceCount,
    DieByDifficulty DieByDifficulty,
    int TechRerollWindowHours,
    int ChallengeExtraDice = 0,
    CoinReward? Coins = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RunZone? Zone = null);

/// <summary>
/// The zone the player stood in at the roll, with the rules of it the run plays by (SPEC «Зона фиксируется в момент
/// ролла», D-307): the dice modifier and the drop penalty multiplier. Null — the roll was outside any zone; then the
/// snapshot is written as before zones existed.
/// </summary>
public sealed record RunZone(string Id, Content.DiceModifierSpec? DiceModifier = null, decimal? DropPenaltyMultiplier = null)
{
    /// <summary>Dice of the run's die added after the count's limit (<c>count</c>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int ExtraDice => DiceModifier is { Stage: Content.DiceStage.Count, Value: { Kind: Content.ContentValueKind.Number } value } ? value.Number : 0;

    /// <summary>Points and steps added to the dice sum (<c>add</c>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int AddedToSum => DiceModifier is { Stage: Content.DiceStage.Add, Value: { Kind: Content.ContentValueKind.Number } value } ? value.Number : 0;
}

/// <summary>A review of a completed run (SPEC «Отзыв»): a rating 1–10 and an optional text.</summary>
public sealed record RunReview(int Rating, string? Text);

/// <summary>
/// An attempt to complete one rolled game. <see cref="CompletedAt"/> orders the proof queue; <see cref="ReachedFinish"/>
/// says this run's latest move stands on the finish; <see cref="Moved"/> is the net
/// steps its moves took, which a reject takes back (D-98). <see cref="AfterFinish"/>: completed after its player had
/// finished, so it does not count for the position (Q-3); <see cref="FreeMode"/>: completed while its player was first,
/// so it does not make the game completed in the season (D-16).
/// </summary>
public sealed record RunState(
    Guid RunId,
    Guid PlayerId,
    Guid GameId,
    RunStatus Status,
    RunSnapshot Snapshot,
    DateTimeOffset RolledAt,
    DateTimeOffset StartedAt,
    Difficulty? Difficulty,
    decimal? Hours,
    EquatableArray<Die> Dice,
    EquatableArray<Die> ChallengeDice = default,
    string? HoursSource = null,
    RunReview? Review = null,
    DateTimeOffset? CompletedAt = null,
    bool ReachedFinish = false,
    Proofs.ProofState? Proof = null,
    int Moved = 0,
    bool AfterFinish = false,
    bool FreeMode = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RunDiceMods? Mods = null)
{
    /// <summary>What items and effects did to the throw (D-408); none for a run without them.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public RunDiceMods DiceMods => Mods ?? RunDiceMods.None;
}
