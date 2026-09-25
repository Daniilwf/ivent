namespace GameEvent.Engine.Content;

/// <summary>A map zone (CONTENT.md «Зона»): its roll filter, dice change, drop penalty, deck and shop prices (stage 2).</summary>
public sealed record ZoneDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public GameFilterSpec? RollFilter { get; init; }

    public DiceModifierSpec? DiceModifier { get; init; }

    public decimal? DropPenaltyMultiplier { get; init; }

    public string? Deck { get; init; }

    public decimal? ShopPriceMultiplier { get; init; }
}

/// <summary>A stage of the dice pipeline and its value, as a zone applies it to every roll inside.</summary>
public sealed record DiceModifierSpec
{
    public required DiceStage Stage { get; init; }

    public required ContentValue Value { get; init; }
}

/// <summary>What a special cell does (CONTENT.md «клетки»); the plain start, empty and finish cells are the map's own.</summary>
public enum ContentCellType
{
    Shop,
    Event,
    Teleport,
    PointsBonus,
    Checkpoint,
}

/// <summary>A special cell of the map (stage 2): what it grants, which deck it draws, where it teleports, how many points.</summary>
public sealed record CellDefinition
{
    public required string Id { get; init; }

    public required ContentCellType Type { get; init; }

    public string? Grants { get; init; }

    public string? Deck { get; init; }

    public string? To { get; init; }

    public int? Amount { get; init; }
}

public enum PollVoters
{
    Players,
    Everyone,
}

/// <summary>A poll (CONTENT.md «голосование», stage 6): the question, the options, who votes, and the effect of the result.</summary>
public sealed record PollDefinition
{
    public required string Question { get; init; }

    public required ChoiceOptionsSpec Options { get; init; }

    public required PollVoters Voters { get; init; }

    public bool Anonymous { get; init; }

    public required int ClosesInHours { get; init; }

    public EffectSpec? OnResult { get; init; }
}

public enum ChallengeCheck
{
    Auto,
    Manual,
}

/// <summary>What a weekly challenge pays.</summary>
public sealed record RewardSpec
{
    public int? Coins { get; init; }
}

/// <summary>A weekly challenge (CONTENT.md «челлендж недели», stage 6).</summary>
public sealed record ChallengeDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required ConditionSpec Condition { get; init; }

    public required RewardSpec Reward { get; init; }

    public required ChallengeCheck Check { get; init; }
}
