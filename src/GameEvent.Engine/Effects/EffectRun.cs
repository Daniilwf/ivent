using System.Globalization;
using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Effects;

/// <summary>Where a <c>modifyDice</c> of this run of an effect lands (D-408).</summary>
public enum DiceScope
{
    /// <summary>Nothing is being thrown: both <c>current</c> and <c>next</c> wait for the next throw.</summary>
    Pending,

    /// <summary>A throw is being made (the <c>beforeDice</c> trigger): <c>current</c> changes it.</summary>
    Throw,

    /// <summary>A throw was just made (the <c>afterDice</c> window or trigger): <c>current</c> changes it after the fact.</summary>
    AfterThrow,
}

/// <summary>A change of the throw being made, collected while <c>beforeDice</c> effects fire (D-408).</summary>
internal sealed record ThrowChange(string ObjectId, DiceStage Stage, ContentValue Value);

/// <summary>
/// One run of an effect (SPEC «Архитектура движка правил»: trigger, condition, targets, actions): the actions of an
/// effect on each target in order, with the references of CONTENT.md — <c>$roll</c>, <c>$choice</c> and the object's
/// parameters. Events are applied as they are written, so each action sees what the ones before it did. A problem the
/// player could fix (a wrong answer to a choice) is a <see cref="Refusal"/>; everything else that cannot apply is skipped.
/// </summary>
internal sealed class EffectRun
{
    private readonly IReadOnlyList<string> _choices;
    private int _choiceIndex;

    public EffectRun(
        SeasonState state,
        EngineContext context,
        Guid userId,
        string objectId,
        bool hostile,
        ContentParamDictionary? parameters,
        IReadOnlyList<string>? choices = null,
        DiceScope dice = DiceScope.Pending,
        Guid? runId = null)
    {
        State = state;
        Context = context;
        UserId = userId;
        ObjectId = objectId;
        Hostile = hostile;
        Parameters = parameters;
        _choices = choices ?? [];
        Dice = dice;
        RunId = runId;
    }

    public SeasonState State { get; private set; }

    public EngineContext Context { get; }

    /// <summary>Who used the item or owns the effect.</summary>
    public Guid UserId { get; }

    /// <summary>The object whose effect this is.</summary>
    public string ObjectId { get; }

    public bool Hostile { get; }

    public ContentParamDictionary? Parameters { get; }

    public DiceScope Dice { get; }

    /// <summary>The run whose throw is being made or was just made (<see cref="DiceScope.Throw"/>, <see cref="DiceScope.AfterThrow"/>).</summary>
    public Guid? RunId { get; }

    /// <summary>The target the actions run on now.</summary>
    public Guid TargetId { get; private set; }

    /// <summary>The last roll of the effect on this target (<c>$roll</c>).</summary>
    public int? LastRoll { get; set; }

    /// <summary>The last answer of the player (<c>$choice</c>).</summary>
    public string? LastChoice { get; private set; }

    public List<IGameEvent> Events { get; } = [];

    /// <summary>Changes of the throw being made (<see cref="DiceScope.Throw"/>).</summary>
    public List<ThrowChange> ThrowChanges { get; } = [];

    /// <summary>Set when the command must be refused: the answers do not fit.</summary>
    public Decision? Refusal { get; private set; }

    /// <summary>The answers left unused after the run: a command giving more answers than asked is refused.</summary>
    public int UnusedChoices => _choices.Count - _choiceIndex;

    public void Emit(IGameEvent e)
    {
        Events.Add(e);
        State = SeasonEngine.Apply(State, e);
    }

    public void Emit(IEnumerable<IGameEvent> events)
    {
        foreach (var e in events)
        {
            Emit(e);
        }
    }

    public void Refuse(string code, string detail) => Refusal ??= Decision.Reject(code, detail);

    /// <summary>The player an action works on: the effect's target, or the user for <c>"target": "self"</c>.</summary>
    public Guid ActionTarget(ActionSpec action) => action.Target == Content.ActionTarget.Self ? UserId : TargetId;

    /// <summary>The next answer of the player, checked against <paramref name="options"/>; null (and a refusal) when it does not fit.</summary>
    public string? NextChoice(IReadOnlyCollection<string> options)
    {
        if (_choiceIndex >= _choices.Count)
        {
            Refuse(RejectionCodes.ItemInvalidChoice, $"The effect of «{ObjectId}» asks for choice number {_choiceIndex + 1}.");
            return null;
        }

        var answer = _choices[_choiceIndex++];
        if (!options.Contains(answer, StringComparer.Ordinal))
        {
            Refuse(RejectionCodes.ItemInvalidChoice, $"«{answer}» is not one of the options of «{ObjectId}».");
            return null;
        }

        LastChoice = answer;
        return answer;
    }

    /// <summary>A value as a number (CONTENT.md «Значения»): dice are rolled now and logged, references resolved.</summary>
    public int Number(ContentValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (value.Kind)
        {
            case ContentValueKind.Number:
                return value.Number;
            case ContentValueKind.Dice:
                var dice = CompletionRoll.Roll(value.Count, value.Sides, Context.Random);
                var total = dice.Sum(d => d.Value);
                Emit(new EffectRolled(UserId, ObjectId, dice, total));
                return value.Negative ? -total : total;
            default:
                var text = Reference(value.Name!);
                var number = int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                return value.Negative ? -number : number;
        }
    }

    /// <summary>A value with its references resolved but dice kept (a <c>next</c> dice change rolls when the throw happens).</summary>
    public ContentValue Resolved(ContentValue value) =>
        value.Kind == ContentValueKind.Reference ? ContentValue.Of(Number(value)) : value;

    /// <summary>Text with a reference resolved: <c>$choice</c>, <c>$roll</c> or a parameter; anything else is kept.</summary>
    public string Text(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return ContentValue.TryParse(raw) is { Kind: ContentValueKind.Reference, Name: { } name } reference
            ? (reference.Negative ? "-" : "") + Reference(name)
            : raw;
    }

    private string Reference(string name) =>
        name switch
        {
            "roll" => (LastRoll ?? 0).ToString(CultureInfo.InvariantCulture),
            "choice" => LastChoice ?? "",
            _ => Parameters?.GetValueOrDefault(name) ?? "",
        };

    /// <summary>
    /// The effect on each target (D-407): a hostile effect of another player may be intercepted; otherwise it counts
    /// as received, then the actions or the outcomes run.
    /// </summary>
    public void Run(EffectSpec effect, IReadOnlyList<Guid> targets)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(targets);
        foreach (var target in targets)
        {
            TargetId = target;
            LastRoll = null;
            _choiceIndex = 0;
            if (Hostile && target != UserId)
            {
                if (Interception.Find(State, target) is { } shield)
                {
                    Emit(Interception.Stop(State, target, shield, ObjectId, UserId));
                    continue;
                }

                Emit(new HostileReceived(target, UserId, ObjectId));
            }

            if (effect.Outcomes is { } outcomes)
            {
                LastRoll = Number(outcomes.Roll);
                var roll = LastRoll.Value;
                RunActions(outcomes.Cases.FirstOrDefault(c => c.From <= roll && roll <= c.To)?.Actions ?? []);
            }
            else
            {
                RunActions(effect.Actions);
            }

            if (Refusal is not null)
            {
                return;
            }
        }
    }

    private void RunActions(EquatableArray<ActionSpec> actions)
    {
        foreach (var action in actions)
        {
            Actions.BaseActionRegistry.ByType[action.GetType()].Execute(action, this);
            if (Refusal is not null)
            {
                return;
            }
        }
    }
}
