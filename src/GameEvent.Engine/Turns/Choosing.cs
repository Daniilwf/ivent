using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Turns;

internal static class Choosing
{
    public static Decision Decide(SeasonState state, MakeChoice command) =>
        throw new NotImplementedException("C4");

    public static SeasonState Apply(SeasonState state, GameChoiceRolled e) =>
        throw new NotImplementedException("C4");

    public static SeasonState Apply(SeasonState state, ChoiceMade e) =>
        throw new NotImplementedException("C4");

    public static SeasonState Apply(SeasonState state, ChoiceDiscarded e) =>
        throw new NotImplementedException("C4");
}
