using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Runs;

internal static class RunLifecycle
{
    public static Decision Decide(SeasonState state, StartRun command, EngineContext context) =>
        throw new NotImplementedException("B1");

    public static Decision Decide(SeasonState state, CompleteRun command, EngineContext context) =>
        throw new NotImplementedException("B1");

    public static SeasonState Apply(SeasonState state, RunStarted e) =>
        throw new NotImplementedException("B1");

    public static SeasonState Apply(SeasonState state, RunCompleted e) =>
        throw new NotImplementedException("B1");

    public static SeasonState Apply(SeasonState state, CompletionDiceRolled e) =>
        throw new NotImplementedException("B1");
}
