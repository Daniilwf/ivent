using System.Text.Json.Serialization;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Content;

/// <summary>What an object is (CONTENT.md «Определение объекта», GLOSSARY).</summary>
public enum ObjectKind
{
    Item,
    Effect,
    SpecialRoll,
    Event,
    Achievement,
}

public enum Rarity
{
    Common,
    Epic,
    Legendary,
}

/// <summary>When an item may be used (SPEC «Окна использования»).</summary>
public enum UseWindow
{
    BeforeRoll,
    AfterRoll,
    BeforeDice,
    AfterDice,
    Anytime,
}

/// <summary>What an IRL event needs as a proof.</summary>
public enum ContentProof
{
    Media,
}

public enum AchievementScope
{
    Season,
    AllTime,
}

/// <summary>When an effect fires (CONTENT.md «Блок effect»: <c>trigger</c>).</summary>
public enum Trigger
{
    BeforeRoll,
    AfterRoll,
    RunCompleted,
    BeforeDice,
    AfterDice,
    MoveStep,
    Stop,
    Pass,
    Drop,
    TechReroll,
    Time,
    HostileIncoming,
}

/// <summary>Whom an effect targets (SPEC «Селекторы»).</summary>
public enum TargetSelector
{
    Self,
    Chosen,
    RandomActive,
    All,
    Leader,
    HigherPoints,
    LowerPoints,
}

/// <summary>Narrows <see cref="TargetSelector.Chosen"/> and <see cref="TargetSelector.RandomActive"/>.</summary>
public enum Among
{
    HigherPoints,
    LowerPoints,
}

public enum Intercept
{
    Hostile,
}

/// <summary>
/// An item, effect, special roll, event or achievement as content (CONTENT.md «Определение объекта», SPEC «ObjectDefinition»).
/// Behaviour comes with stages 4–6; stage 1 loads and checks the format (CT1). <see cref="Price"/> null — not sold.
/// </summary>
public sealed record ObjectDefinition
{
    public required string Id { get; init; }

    public required ObjectKind Kind { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public Rarity? Rarity { get; init; }

    public int? Price { get; init; }

    public UseWindow? Window { get; init; }

    /// <summary>Against another player: counted by interception, limits and attack statistics.</summary>
    public bool Hostile { get; init; }

    /// <summary>Several of the same may be used at once.</summary>
    public bool Stackable { get; init; }

    /// <summary>No automation: the effect goes to manual resolution.</summary>
    public bool Manual { get; init; }

    public ContentProof? Proof { get; init; }

    /// <summary>Achievements only: counted per season or over all seasons.</summary>
    public AchievementScope? Scope { get; init; }

    /// <summary>What happens; may be missing on a manual object.</summary>
    public EffectSpec? Effect { get; init; }
}

/// <summary>The <c>effect</c> block (CONTENT.md «Блок effect»).</summary>
public sealed record EffectSpec
{
    public TargetSpec? Target { get; init; }

    public Trigger? Trigger { get; init; }

    public DurationSpec? Duration { get; init; }

    public ConditionSpec? Condition { get; init; }

    public Intercept? Intercept { get; init; }

    public EquatableArray<ActionSpec> Actions { get; init; } = [];

    /// <summary>A random branch: the roll picks the case whose range holds it.</summary>
    public OutcomesSpec? Outcomes { get; init; }
}

public sealed record TargetSpec
{
    public required TargetSelector Selector { get; init; }

    public Among? Among { get; init; }

    public bool ExcludeSelf { get; init; }
}

/// <summary>How long an effect lives: exactly one of the fields.</summary>
public sealed record DurationSpec
{
    public int? Uses { get; init; }

    public int? Hours { get; init; }

    public int? Runs { get; init; }

    public bool? UntilTriggered { get; init; }
}

/// <summary>
/// A player statistic a condition may test (CONTENT.md «Статистика игрока»); each new one is its own task with a test.
/// </summary>
public enum ContentStat
{
    /// <summary>Hostile effects received this season; needs <c>gte</c>.</summary>
    HostileReceived,

    /// <summary>Completed runs in a row with <c>tag</c>; needs <c>tag</c> and <c>gte</c>.</summary>
    CompletedStreakWithTag,

    /// <summary>Every die of the roll at its maximum; needs <c>minDice</c>.</summary>
    AllDiceMax,

    /// <summary>The run's hours; needs <c>gte</c>.</summary>
    RunHours,
}

/// <summary>
/// A condition (CONTENT.md «Условия и фильтры»): on the run (<see cref="DifficultyAtLeast"/>), on the game
/// (<see cref="Game"/>) or on a player statistic (<see cref="Stat"/> with its parameters).
/// </summary>
public sealed record ConditionSpec
{
    public Runs.Difficulty? DifficultyAtLeast { get; init; }

    public GameFilterSpec? Game { get; init; }

    public ContentStat? Stat { get; init; }

    public string? Tag { get; init; }

    public int? Gte { get; init; }

    public int? MinDice { get; init; }
}

/// <summary>A game filter: tags (a <c>$parameter</c> allowed), hours, release year.</summary>
public sealed record GameFilterSpec
{
    public EquatableArray<string>? Tags { get; init; }

    public decimal? MaxHours { get; init; }

    public decimal? MinHours { get; init; }

    public int? ReleaseYearBefore { get; init; }
}

public sealed record OutcomesSpec
{
    public required ContentValue Roll { get; init; }

    public required EquatableArray<OutcomeCase> Cases { get; init; }
}

public sealed record OutcomeCase
{
    public required int From { get; init; }

    public required int To { get; init; }

    public EquatableArray<ActionSpec> Actions { get; init; } = [];
}

/// <summary>Overrides the action's target: only <c>self</c> (CONTENT.md «Цель действия»).</summary>
public enum ActionTarget
{
    Self,
}

/// <summary>
/// A base action (CONTENT.md «Базовые действия»): at most 12 kinds (invariant 7), each a data record chosen by <c>type</c>.
/// <see cref="Target"/> <c>self</c> points the action at the one who used the object instead of the effect's target.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(MoveAction), "move")]
[JsonDerivedType(typeof(ChangeResourceAction), "changeResource")]
[JsonDerivedType(typeof(RollAction), "roll")]
[JsonDerivedType(typeof(GiveObjectAction), "giveObject")]
[JsonDerivedType(typeof(TakeObjectAction), "takeObject")]
[JsonDerivedType(typeof(TransformObjectAction), "transformObject")]
[JsonDerivedType(typeof(DrawEventAction), "drawEvent")]
[JsonDerivedType(typeof(SpinWheelAction), "spinWheel")]
[JsonDerivedType(typeof(ModifyNextRollAction), "modifyNextRoll")]
[JsonDerivedType(typeof(ModifyDiceAction), "modifyDice")]
[JsonDerivedType(typeof(TeleportAction), "teleport")]
[JsonDerivedType(typeof(RequestChoiceAction), "requestChoice")]
public abstract record ActionSpec
{
    public ActionTarget? Target { get; init; }
}

/// <summary>Move by <see cref="Steps"/> cells; a minus goes back.</summary>
public sealed record MoveAction : ActionSpec
{
    public required ContentValue Steps { get; init; }
}

/// <summary>Change <c>points</c>, <c>coins</c> or another resource by <see cref="Amount"/>.</summary>
public sealed record ChangeResourceAction : ActionSpec
{
    public required string Resource { get; init; }

    public required ContentValue Amount { get; init; }
}

/// <summary>Roll dice; the result is <c>$roll</c> for the next actions.</summary>
public sealed record RollAction : ActionSpec
{
    public required ContentValue Dice { get; init; }
}

/// <summary>Give an object; <see cref="Params"/> fill its <c>$parameters</c>: plain text or a reference such as <c>$choice</c>.</summary>
public sealed record GiveObjectAction : ActionSpec
{
    public required string ObjectId { get; init; }

    public ContentParamDictionary? Params { get; init; }
}

public enum TakeMode
{
    Take,
    Steal,
    Destroy,
}

public enum Pick
{
    Random,
    Chosen,
}

/// <summary>Which objects of an inventory an action may take or change.</summary>
public sealed record ObjectFilterSpec
{
    public ObjectKind? Kind { get; init; }

    public Rarity? Rarity { get; init; }

    public string? ObjectId { get; init; }

    public bool? Hostile { get; init; }
}

public sealed record TakeObjectAction : ActionSpec
{
    public required TakeMode Mode { get; init; }

    public ObjectFilterSpec? Filter { get; init; }

    public Pick Pick { get; init; } = Pick.Random;
}

public enum TransformMode
{
    Transform,
    Annotate,
}

/// <summary>Turn an object into another (<see cref="Into"/>) or add a note to it.</summary>
public sealed record TransformObjectAction : ActionSpec
{
    public required TransformMode Mode { get; init; }

    public ObjectFilterSpec? Filter { get; init; }

    public string? Into { get; init; }

    public string? Note { get; init; }
}

/// <summary>Draw an event from a deck: <c>good</c>, <c>bad</c>, <c>special</c>, <c>risky</c>, <c>zone</c> or a deck id.</summary>
public sealed record DrawEventAction : ActionSpec
{
    public required string Deck { get; init; }
}

public sealed record SpinWheelAction : ActionSpec
{
    public required string Wheel { get; init; }
}

/// <summary>Change the next roll: a filter, a choice of several games, a condition on the run.</summary>
public sealed record ModifyNextRollAction : ActionSpec
{
    public GameFilterSpec? Filter { get; init; }

    public int? ChoiceCount { get; init; }

    public ConditionSpec? RunCondition { get; init; }
}

public enum DiceWhen
{
    Current,
    Next,
}

/// <summary>A stage of the dice pipeline (D-14).</summary>
public enum DiceStage
{
    Count,
    Sides,
    Add,
    Multiply,
    Reroll,
    Min,
    Max,
}

public sealed record ModifyDiceAction : ActionSpec
{
    public required DiceWhen When { get; init; }

    public required DiceStage Stage { get; init; }

    public required ContentValue Value { get; init; }
}

/// <summary>Move without walking to a cell id or <c>nearestShortcut</c>.</summary>
public sealed record TeleportAction : ActionSpec
{
    public required string Cell { get; init; }
}

public enum ChoiceSource
{
    Categories,
    Players,
    Games,
}

/// <summary>The options of a choice: from a source or a fixed list.</summary>
public sealed record ChoiceOptionsSpec
{
    public ChoiceSource? From { get; init; }

    public EquatableArray<string>? List { get; init; }
}

/// <summary>Ask the player to choose; the answer is <c>$choice</c>.</summary>
public sealed record RequestChoiceAction : ActionSpec
{
    public required string Prompt { get; init; }

    public required ChoiceOptionsSpec Options { get; init; }
}
