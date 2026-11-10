using GameEvent.Simulator.Setup;

namespace GameEvent.Simulator.Play;

/// <summary>How a run of a bot ended.</summary>
public enum RunEnd
{
    Completed,
    Dropped,
    TechRerolled,

    /// <summary>Still being played at the deadline: it does not count.</summary>
    Unfinished,

    /// <summary>Completed, then rejected by the admin bot.</summary>
    Rejected,

    /// <summary>Given up by wish: a tech reroll with the reason «wish» (D-325).</summary>
    WishRerolled,
}

/// <summary>
/// One run: the game's hours (from the snapshot), the hours the bot played it, how it ended, the points its completion
/// gave (less what a reject took back) and the drop penalty it took, the zone it was rolled in, and whether it was played in the first's free mode
/// (no points). <see cref="DropStreak"/> — for a drop, the drops in a row before it (D-324); <see cref="WishListed"/> — the
/// game could be given up by wish (D-325); <see cref="MoveCells"/> — the cells its completion moved the token forward;
/// for a reject, <see cref="RejectCells"/> — the cells it moved the token back, <see cref="RejectedAfterFinish"/> — the
/// player had a finish then, <see cref="RejectedWhileFrozen"/> — he was the frozen first then (D-99),
/// <see cref="PenalizedReject"/> — it came with the drop penalty (D-327), <see cref="RejectTaken"/> — the points it took
/// back (the dice and the bonus of the cell the move stopped on).
/// </summary>
public sealed record RunOutcome(
    int Bot,
    double Hours,
    double PlayHours,
    RunEnd End,
    int Points,
    int Penalty,
    string? Zone,
    bool FreeMode,
    int? DropStreak = null,
    bool WishListed = false,
    int MoveCells = 0,
    int RejectCells = 0,
    bool RejectedAfterFinish = false,
    bool PenalizedReject = false,
    int RejectTaken = 0,
    bool RejectedWhileFrozen = false);

/// <summary>A bot's branch at a fork (<see cref="Fork"/> — the fork cell, <see cref="Option"/> — the cell of the branch).</summary>
public sealed record BranchOutcome(int Bot, string Fork, string Option);

/// <summary>
/// A bot at the end of the season: its profile and policies, its place and points in the result, its finish
/// (<see cref="FinishedDay"/> — days from the start when the finish that stood was reached), what it did, and
/// <see cref="BlockedHours"/> — free time lost waiting for the admin's check (<c>season.maxUncheckedRuns</c>).
/// <see cref="Rejected"/> — its commands the engine refused. Points are the engine's; they add up from the runs' points
/// (a reject's take-back included), the drop penalties, <see cref="CellBonus"/>, <see cref="FinishBonus"/> and
/// <see cref="OtherPoints"/> (any other change, such as an admin's). <see cref="WishRerolls"/> — games given up by wish (not in
/// <see cref="TechRerolls"/>), <see cref="KeptForStreak"/> — long games a dropper kept because of its drops in a row (D-362),
/// <see cref="PenalizedRejects"/> — its runs rejected with the drop penalty.
/// </summary>
public sealed record BotOutcome(
    int Index,
    string Profile,
    bool Dropper,
    BranchPolicy BranchPolicy,
    int Points,
    int Place,
    bool IsFirst,
    int? FinishOrder,
    double? FinishedDay,
    int Completed,
    int Drops,
    int TechRerolls,
    int AlreadyPlayed,
    int FreeRerolls,
    int PaidRerolls,
    double PlayHours,
    double FreeHours,
    double BlockedHours,
    int Rejected,
    int CellBonus,
    int FinishBonus,
    int OtherPoints,
    int Teleports,
    int WishRerolls = 0,
    int KeptForStreak = 0,
    int PenalizedRejects = 0);

/// <summary>
/// One simulated season. <see cref="FirstFinishDay"/> — when the player who ended up first reached the finish;
/// <see cref="FirstFrozenDay"/> — when he was frozen (his finish approved). <see cref="Rejections"/> — refused commands by
/// code; <see cref="GuardTrips"/> — times a bot hit the action limit of one wake (0 unless a bot loops).
/// </summary>
public sealed record SeasonOutcome(
    int Index,
    ulong Seed,
    IReadOnlyList<BotOutcome> Bots,
    IReadOnlyList<RunOutcome> Runs,
    IReadOnlyList<BranchOutcome> Branches,
    double? FirstFinishDay,
    double? FirstFrozenDay,
    IReadOnlyDictionary<string, int> Rejections,
    int Commands,
    int Events,
    int Misses,
    int GuardTrips);
