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
/// the coin reward for completing (D-96). <see cref="ImposedTags"/> — the genres the roll's own filters imposed (a zone's tags
/// now, special rolls and events later): no wish reroll then (D-325); <see cref="WishRerollTags"/> — <c>roll.wishRerollTags</c>
/// at the roll, the genres this run may be given up for by wish (D-325). Both written only when there are any.
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
    RunZone? Zone = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    EquatableArray<string> ImposedTags = default,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    EquatableArray<string> WishRerollTags = default)
{
    /// <summary>The conditions of events on this run, fixed at the roll (D-507); written only when there are any.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public EquatableArray<RunCondition> Conditions { get; init; }

    /// <summary><c>reward.unmetConditionPolicy</c> at the roll (D-546 (2)); only with conditions.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public UnmetConditionPolicy? ConditionPolicy { get; init; }
}

/// <summary>
/// A condition of an event on a run (SPEC «Условия от ивентов», D-507): whose it is, its bounds of difficulty, and whether
/// another player put it (a hostile change, D-425).
/// </summary>
public sealed record RunCondition(
    string ObjectId,
    Content.ConditionSpec Condition,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool Hostile = false);

/// <summary>
/// The check of a completed run's conditions (D-507): <see cref="Met"/> at the difficulty that counts, <see cref="Waived"/>
/// by the admin as not applicable (D-546 (2)), <see cref="ChallengeDone"/> claimed at the completion — so dice thrown later,
/// when the condition becomes met, include the challenge's; <see cref="Held"/> — the thrown dice an unmet condition took.
/// </summary>
public sealed record RunConditionState(
    bool Met,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool Waived = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool ChallengeDone = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RunHeldDice? Held = null);

/// <summary>The thrown dice an unmet condition took off a run (D-593), given back when it is met again.</summary>
public sealed record RunHeldDice(EquatableArray<Die> Dice, EquatableArray<Die> ChallengeDice, RunDiceMods? Mods);

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
/// cells its moves walked, which a correction takes back (D-98); a reject takes back <see cref="Gain"/> (D-321). <see cref="AfterFinish"/>: completed after its player had
/// finished, so it does not count for the position (Q-3); <see cref="FreeMode"/>: completed while its player was first,
/// so it does not make the game completed in the season (D-16). <see cref="MoveFrom"/> — the cell the run's own move
/// began on; <see cref="Gain"/> — how many cells closer to the finish its moves brought the player, the teleport its own
/// move stopped on included (negative — further, a snake): what a reject takes back (D-321). <see cref="CellPoints"/> — the
/// points bonus of the cell the run's own move stopped on, which a reject takes back with the dice (D-327);
/// <see cref="CellObjects"/> — the objects the cells of that move granted (a shop's coupon), which a reject takes back
/// when the player still holds them (D-327 (2), D-421). <see cref="DeclaredDifficulty"/> — the difficulty named at the
/// start, the cap of the one claimed at the completion (D-633); null — none named.
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

    // Written only when set: undo snapshots of runs made before them keep their format (D-321)
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? MoveFrom = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int Gain = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int CellPoints = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RunDiceMods? Mods = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    EquatableArray<Guid> CellObjects = default,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Difficulty? DeclaredDifficulty = null,

    // The run drew its difficulty's event (D-557): never a second one, whatever the admin later does to its difficulty
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool DifficultyEventDrawn = false,

    // The check of its conditions (D-507): null for a run without any, or not completed yet
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RunConditionState? Condition = null,

    // The partner's side of a coop run (D-520): null for a run played alone
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    CoopPartner? Coop = null)
{
    /// <summary>The coop partner (D-520); null for a run played alone.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? PartnerId => Coop?.PlayerId;

    /// <summary>
    /// The run as <paramref name="playerId"/>'s side of it sees it (D-545): for the coop partner, his own bookkeeping of the
    /// run's move (<see cref="CoopPartner"/>) in place of the roller's, under his id; for anybody else the run itself. A view
    /// to read: written back only through <see cref="WithSide"/>.
    /// </summary>
    public RunState Side(Guid playerId) =>
        Coop is { } c && c.PlayerId == playerId && playerId != PlayerId
            ? this with
            {
                PlayerId = playerId,
                AfterFinish = c.AfterFinish,
                Moved = c.Moved,
                ReachedFinish = c.ReachedFinish,
                MoveFrom = c.MoveFrom,
                Gain = c.Gain,
                CellPoints = c.CellPoints,
                CellObjects = c.CellObjects,
            }
            : this;

    /// <summary>The run with <paramref name="playerId"/>'s side of it replaced by <paramref name="side"/> (a changed <see cref="Side"/>).</summary>
    public RunState WithSide(Guid playerId, RunState side)
    {
        ArgumentNullException.ThrowIfNull(side);
        return Coop is { } c && c.PlayerId == playerId && playerId != PlayerId
            ? this with
            {
                Coop = c with
                {
                    AfterFinish = side.AfterFinish,
                    Moved = side.Moved,
                    ReachedFinish = side.ReachedFinish,
                    MoveFrom = side.MoveFrom,
                    Gain = side.Gain,
                    CellPoints = side.CellPoints,
                    CellObjects = side.CellObjects,
                },
            }
            : side with { PlayerId = PlayerId, Coop = Coop };
    }

    /// <summary>What items and effects did to the throw (D-408); none for a run without them.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public RunDiceMods DiceMods => Mods ?? RunDiceMods.None;
}

/// <summary>
/// The partner's side of a coop run (D-520, D-521, D-545): who and by which invitation; once completed, the shares it was split
/// by (<see cref="Shares"/>) and the partner's own bookkeeping of his move, as <see cref="RunState"/> keeps the roller's — whether
/// the run came after his finish, the cells it moved him, whether that move stands on the finish, the cell it began on, what it
/// brought him closer, the bonus and the objects of the cell he stopped on (D-321, D-327).
/// </summary>
public sealed record CoopPartner(
    Guid PlayerId,
    Guid InteractionId,
    DateTimeOffset JoinedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    CoopShareRules? Shares = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool AfterFinish = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int Moved = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool ReachedFinish = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? MoveFrom = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int Gain = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int CellPoints = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    EquatableArray<Guid> CellObjects = default);

/// <summary>The shares a completed coop run was split by (<c>reward.coop</c> at the completion, D-521).</summary>
public sealed record CoopShareRules(decimal PointsShare, decimal CoinsShare);
