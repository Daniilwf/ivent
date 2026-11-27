namespace GameEvent.Infrastructure.Seasons;

public sealed class PlayerAchievementRecord
{
    public Guid PlayerId { get; init; }
    public required string ObjectId { get; init; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public DateTimeOffset EarnedAt { get; set; }

    /// <summary>Award order in the player's state, including awards with equal timestamps.</summary>
    public int Position { get; set; }
}
