using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Seasons;

/// <summary>Everything a command may consult besides the season state (D-01).</summary>
public sealed record EngineContext(IClock Clock, IRandomSource Random, IIdGenerator Ids, Ruleset Ruleset, IPoolView Pool);
