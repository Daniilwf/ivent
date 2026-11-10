using System.Collections.Frozen;
using GameEvent.Engine.Content;

namespace GameEvent.Engine.Effects.Actions;

/// <summary>
/// One base action (SPEC «Базовые действия», invariant 7): a handler class per action type, its parameters described by
/// the action's record (the JSON schema of content is generated from it, D-411). There are at most 12.
/// </summary>
internal interface IActionHandler
{
    Type Action { get; }

    void Execute(ActionSpec action, EffectRun run);
}

/// <summary>A handler of one action record type.</summary>
internal abstract class ActionHandler<T> : IActionHandler
    where T : ActionSpec
{
    public Type Action => typeof(T);

    public void Execute(ActionSpec action, EffectRun run) => Execute((T)action, run);

    protected abstract void Execute(T action, EffectRun run);
}

internal static class BaseActionRegistry
{
    /// <summary>The handlers, one per base action.</summary>
    public static IReadOnlyList<IActionHandler> All { get; } =
    [
        new MoveHandler(),
        new ChangeResourceHandler(),
        new RollHandler(),
        new GiveObjectHandler(),
        new TakeObjectHandler(),
        new TransformObjectHandler(),
        new DrawEventHandler(),
        new SpinWheelHandler(),
        new ModifyNextRollHandler(),
        new ModifyDiceHandler(),
        new TeleportHandler(),
        new RequestChoiceHandler(),
    ];

    public static FrozenDictionary<Type, IActionHandler> ByType { get; } = All.ToFrozenDictionary(h => h.Action);
}
