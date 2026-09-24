namespace GameEvent.Engine.Kernel;

/// <summary>
/// A fact in the season log. Events store results (dice values, the chosen game, wheel misses),
/// never intentions, so replaying the log is deterministic. Every event type carries
/// <see cref="EventTypeAttribute"/> with its stable name and format version.
/// </summary>
public interface IGameEvent;
