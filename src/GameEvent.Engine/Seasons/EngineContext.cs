using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;

namespace GameEvent.Engine.Seasons;

/// <summary>
/// Everything a command may consult besides the season state (D-01). The rules are part of the state:
/// they come from the log (D-82). <see cref="Triggers"/> react to a command's events (D-103); none in stage 1's product.
/// <see cref="History"/> is the season's log by command, given for an undo (D-104). <see cref="TimeZones"/> is the host's
/// time zone database for the season's calendar (D-579); without it nothing that counts calendar days is accepted.
/// </summary>
public sealed record EngineContext(
    IClock Clock,
    IRandomSource Random,
    IIdGenerator Ids,
    IPoolView Pool,
    IReadOnlyList<Effects.ITriggerHandler>? Triggers = null,
    IReadOnlyList<Undo.LoggedCommand>? History = null,
    ITimeZones? TimeZones = null)
{
    /// <summary>The levels of the command's own events above 0, set for each command by <see cref="SeasonEngine.Execute"/> (D-559 (1)).</summary>
    internal Effects.CommandLevels? Levels { get; init; }

    /// <summary>The queue checked that the current reaction's target is a public, active feed row in this season.</summary>
    public bool FeedReactionTargetAvailable { get; init; }

    /// <summary>The queue checked that the comment's feed target is a public, active row in this season.</summary>
    public bool CommentTargetAvailable { get; init; }

    /// <summary>Active account awards supplied by the command queue, including those earned in another season.</summary>
    public IReadOnlySet<Achievements.AccountAchievementKey>? AccountAchievements { get; init; }

    /// <summary>The queue prepared a protected image for this gallery publication.</summary>
    public Guid? PreparedGalleryFileId { get; init; }

    /// <summary>The queue verified the approval command matches the proof attachment prepared for publication.</summary>
    public Guid? GalleryProofApprovalCommandId { get; init; }

    /// <summary>Effective landed attacks loaded by the queue when it records season results.</summary>
    public IReadOnlyList<Inventory.HostileReceived>? EffectiveAttacks { get; init; }

    /// <summary>The queue verified that these candidates of the requested category are public in this season.</summary>
    public IReadOnlySet<Guid>? AvailableNominationCandidates { get; init; }
}
