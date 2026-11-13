using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Effects;

/// <summary>
/// The chain levels of a command's own events that run above level 0 (D-559 (1)): the draw of an event cell a run's own
/// move or a drop's penalty move stops on is made inside the command (D-504, D-553 (2)) but is a reaction to that move, as
/// in the chain (D-500) — its event runs at level 1 and what it writes reacts from level 2, with or without a question.
/// One per command (<see cref="Seasons.SeasonEngine.Execute"/>); the events are marked by reference, as the decision holds
/// them.
/// </summary>
internal sealed class CommandLevels
{
    private readonly Dictionary<IGameEvent, int> _levels = new(ReferenceEqualityComparer.Instance);

    public bool IsEmpty => _levels.Count == 0;

    public void Mark(IEnumerable<IGameEvent> events, int level)
    {
        foreach (var e in events)
        {
            _levels[e] = level;
        }
    }

    public int Of(IGameEvent e) => _levels.GetValueOrDefault(e);
}
