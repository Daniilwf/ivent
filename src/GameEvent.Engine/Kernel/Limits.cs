namespace GameEvent.Engine.Kernel;

/// <summary>Limits shared by the engine and the API, so a request and a command agree on what is too long.</summary>
public static class Limits
{
    /// <summary>A comment in the public log: admin changes, tech reroll reasons.</summary>
    public const int MaxCommentLength = 500;

    /// <summary>The text of a review.</summary>
    public const int MaxReviewLength = 2000;

    /// <summary>Where an hours estimate comes from: a link or a short note.</summary>
    public const int MaxHoursSourceLength = 300;

    /// <summary>Proof links: how many, and how long each.</summary>
    public const int MaxProofLinks = 5;

    /// <summary>Screenshots in one proof (D-116).</summary>
    public const int MaxProofFiles = 5;

    public const int MaxProofLinkLength = 500;

    /// <summary>How deep effects may react to effects in one command (SPEC «Лимит цепочки»).</summary>
    public const int MaxEffectDepth = 3;

    /// <summary>How many events one command may write, its reactions included (SPEC «Лимит цепочки»).</summary>
    public const int MaxEventsPerCommand = 50;
}
