using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Map;

/// <summary>
/// The player's token moved. <see cref="Path"/> lists the cells entered in order, ending at <see cref="To"/>.
/// <see cref="Steps"/> is how many steps were requested; steps beyond the finish burn.
/// </summary>
[EventType("player-moved")]
public sealed record PlayerMoved(Guid PlayerId, string From, string To, int Steps, EquatableArray<string> Path) : IGameEvent;
