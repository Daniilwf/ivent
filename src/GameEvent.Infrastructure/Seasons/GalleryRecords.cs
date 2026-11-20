namespace GameEvent.Infrastructure.Seasons;

/// <summary>Season metadata folded from the log, including items imported without their image.</summary>
public sealed class GalleryMomentRecord
{
    public Guid SeasonId { get; init; }
    public Guid ItemId { get; init; }
    public Guid UserId { get; init; }
    public Guid PlayerId { get; init; }
    public Guid FileId { get; init; }
    public required string Caption { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateOnly CreatedDay { get; init; }
    public bool Hidden { get; set; }
    public Guid? ProofRunId { get; init; }
    public Guid? ProofFileId { get; init; }
    public Guid? ProofApprovalCommandId { get; init; }
    public bool ConsentRevoked { get; set; }
}

/// <summary>Local files belong to exactly one season and gallery publication. The archive does not create bindings.</summary>
public sealed class GalleryMediaBindingRecord
{
    public Guid FileId { get; init; }
    public Guid SeasonId { get; init; }
    public Guid ItemId { get; init; }
}

/// <summary>One selected emoji per account and moment in a season.</summary>
public sealed class GalleryReactionRecord
{
    public Guid SeasonId { get; init; }
    public Guid ItemId { get; init; }
    public Guid UserId { get; init; }
    public required string Emoji { get; init; }
}
