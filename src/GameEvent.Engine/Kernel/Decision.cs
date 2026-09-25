namespace GameEvent.Engine.Kernel;

/// <summary>Outcome of deciding a command: events to append, or a rejection with no events.</summary>
public sealed class Decision
{
    private Decision(IReadOnlyList<IGameEvent> events, Rejection? rejection)
    {
        Events = events;
        Rejection = rejection;
    }

    public IReadOnlyList<IGameEvent> Events { get; }

    public Rejection? Rejection { get; }

    public bool IsAccepted => Rejection is null;

    public static Decision Accept(params IReadOnlyList<IGameEvent> events) => new([.. events], null);

    public static Decision Reject(string code, string detail) => new([], new Rejection(code, detail));

    /// <summary>A refusal naming what it is about, such as the later commands an undo waits for (D-104).</summary>
    public static Decision Reject(string code, string detail, EquatableArray<Guid> related) => new([], new Rejection(code, detail, related));
}
