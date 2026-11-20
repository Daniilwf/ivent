namespace GameEvent.Infrastructure.Seasons;

/// <summary>An active lifetime award and the season player who earned it.</summary>
public sealed class AccountAchievementRecord
{
    public Guid UserId { get; init; }
    public required string ObjectId { get; init; }
    public Guid SeasonId { get; init; }
    public Guid PlayerId { get; init; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public DateTimeOffset EarnedAt { get; set; }
    public int Position { get; set; }
}
