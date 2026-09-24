namespace GameEvent.Engine.Rulesets;

// Season rules. Every game number the engine uses comes from here; there are no balance constants in code.
// Field meanings: docs/RULESET.md. Task C1 completes the types; unknown JSON fields are ignored until then.

public sealed record Ruleset
{
    public required int Version { get; init; }

    public required Features Features { get; init; }

    public required SeasonRules Season { get; init; }

    public required RollRules Roll { get; init; }

    public required RewardRules Reward { get; init; }

    public required MapRules Map { get; init; }
}

public enum MapMode
{
    Linear,
    Graph,
}

public sealed record Features
{
    public required MapMode MapMode { get; init; }
}

public sealed record SeasonRules
{
    public required string Timezone { get; init; }

    public required int MaxActiveRunsPerPlayer { get; init; }
}

public sealed record RollRules
{
    public required int ChoiceCount { get; init; }

    public required int FreeRerollsPerRoll { get; init; }

    public required int TechRerollWindowHours { get; init; }
}

public enum Rounding
{
    Nearest,
    Floor,
    Ceil,
}

public sealed record DiceCountRule
{
    public required decimal HoursPerDie { get; init; }

    public required Rounding Rounding { get; init; }

    public required int Min { get; init; }

    public required int Max { get; init; }
}

public enum EventKind
{
    Good,
    Bad,
}

public sealed record DieRule
{
    public required int Sides { get; init; }

    public EventKind? GrantEvent { get; init; }
}

public sealed record DieByDifficulty
{
    public required DieRule Easy { get; init; }

    public required DieRule Normal { get; init; }

    public required DieRule Hard { get; init; }

    public required DieRule Extreme { get; init; }
}

public sealed record RewardRules
{
    public required DiceCountRule DiceCount { get; init; }

    public required DieByDifficulty DieByDifficulty { get; init; }
}

public sealed record MapRules
{
    public required int LinearLength { get; init; }
}
