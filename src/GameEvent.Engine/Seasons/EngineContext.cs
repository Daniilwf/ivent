using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;

namespace GameEvent.Engine.Seasons;

/// <summary>
/// Everything a command may consult besides the season state (D-01). The rules are part of the state:
/// they come from the log (D-82).
/// </summary>
public sealed record EngineContext(IClock Clock, IRandomSource Random, IIdGenerator Ids, IPoolView Pool);
