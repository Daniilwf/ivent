namespace GameEvent.Engine.Kernel;

/// <summary>Limits shared by the engine and the API, so a request and a command agree on what is too long.</summary>
public static class Limits
{
    /// <summary>A comment in the public log: admin changes, tech reroll reasons.</summary>
    public const int MaxCommentLength = 500;
}
