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
internal sealed record ThrowChange(string ObjectId, DiceStage Stage, ContentValue Value, int? Sides = null, int? From = null, DieFilter? Filter = null);

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

    // What a question would ask now (D-511): the outcome's case or the action being played
    private SuspendedStep _step = SuspendedStep.Action;
    private int _actionIndex;

    // The actions aimed at the user ran: with several targets they run once per use (D-480)
    private bool _selfDone;

    public EffectRun(
        SeasonState state,
        EngineContext context,
        Guid userId,
        string objectId,
        bool hostile,
        ContentParamDictionary? parameters,
        IReadOnlyList<string>? choices = null,
        DiceScope dice = DiceScope.Pending,
        Guid? runId = null,
        int depth = 0,
        int eventsBefore = 0)
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
        Depth = depth;
        EventsBefore = eventsBefore;
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

    /// <summary>
    /// How deep this run is nested in its command (invariant 8): 0 for an item used or a trigger inside a command, the level
    /// of the effect chain for a reaction; a countdown that goes off inside it runs one level deeper.
    /// </summary>
    public int Depth { get; }

    /// <summary>The events the command wrote before this run: with <see cref="Events"/>, what counts towards the 50.</summary>
    public int EventsBefore { get; }

    /// <summary>The events the command has written with this run so far.</summary>
    public int EventsSoFar => EventsBefore + Events.Count;

    /// <summary>Set when a countdown inside the run went past the chain limits: nothing more runs, the cut is the last event.</summary>
    public EffectChainCut? Cut { get; private set; }

    /// <summary>Changes of the throw being made (<see cref="DiceScope.Throw"/>).</summary>
    public List<ThrowChange> ThrowChanges { get; } = [];

    /// <summary>Set when the command must be refused: the answers do not fit.</summary>
    public Decision? Refusal { get; private set; }

    /// <summary>
    /// Set for a drawn event (D-511): a question without an answer suspends the effect with <see cref="EffectSuspended"/>
    /// instead of refusing; null for an item, which answers before its use (D-407).
    /// </summary>
    public Asking? Asks { get; init; }

    /// <summary>
    /// The run goes on after an answer checked against the snapshot of the options (Н-10): an answer that no longer fits what
    /// the action counts now is skipped, not refused.
    /// </summary>
    public bool Resuming { get; init; }

    /// <summary>After the deadline the token does not move (Н-13): <c>move</c> and <c>teleport</c> do nothing.</summary>
    public bool PositionLocked { get; init; }

    /// <summary>Set when the effect stopped to wait for an answer: nothing more of it runs in this command (D-511).</summary>
    public bool Suspended { get; private set; }

    /// <summary>The answers left unused after the run: a command giving more answers than asked is refused.</summary>
    public int UnusedChoices => _choices.Count - _choiceIndex;

    public void Emit(IGameEvent e)
    {
        Cut ??= e as EffectChainCut;
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
        if (_choiceIndex >= _choices.Count && Asks is not null)
        {
            // An event asks and waits; nothing to choose from is nothing to ask (D-511)
            if (options.Count > 0)
            {
                Suspend(_step, options);
            }

            return null;
        }

        if (_choiceIndex >= _choices.Count)
        {
            Refuse(RejectionCodes.ItemInvalidChoice, $"The effect of «{ObjectId}» asks for choice number {_choiceIndex + 1}.");
            return null;
        }

        var answer = _choices[_choiceIndex++];
        if (!options.Contains(answer, StringComparer.Ordinal) && Resuming)
        {
            // Н-10: what was answered is gone since the question: the action is skipped
            return null;
        }

        if (!options.Contains(answer, StringComparer.Ordinal))
        {
            Refuse(RejectionCodes.ItemInvalidChoice, $"«{answer}» is not one of the options of «{ObjectId}».");
            return null;
        }

        LastChoice = answer;
        return answer;
    }

    /// <summary>
    /// Every answer not used yet, now used: a list of preference for a count known only at the use (D-476). Each must be one
    /// of <paramref name="options"/> and given once; otherwise null and a refusal.
    /// </summary>
    public IReadOnlyList<string>? RemainingChoices(IReadOnlyCollection<string> options)
    {
        var answers = _choices.Skip(_choiceIndex).ToList();
        _choiceIndex = _choices.Count;
        if (Asks is not null)
        {
            // An event never asks for a list of preference (the pack refuses it): what is not answered goes at random
            answers = [.. answers.Where(a => options.Contains(a, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)];
        }

        if (answers.FirstOrDefault(a => !options.Contains(a, StringComparer.Ordinal)) is { } wrong)
        {
            Refuse(RejectionCodes.ItemInvalidChoice, $"«{wrong}» is not one of the options of «{ObjectId}».");
            return null;
        }

        if (answers.Distinct(StringComparer.Ordinal).Count() != answers.Count)
        {
            Refuse(RejectionCodes.ItemInvalidChoice, $"An answer to «{ObjectId}» is given twice.");
            return null;
        }

        LastChoice = answers.LastOrDefault() ?? LastChoice;
        return answers;
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

                    // The answers were given for this target: the shield takes them with the effect (IT-1)
                    _choiceIndex = _choices.Count;
                    continue;
                }

                Emit(new HostileReceived(target, UserId, ObjectId));
            }

            if (effect.Outcomes is { Choice: { } choice } byChoice)
            {
                // The player's answer picks the case (D-482)
                _step = SuspendedStep.Outcome;
                if (NextChoice(Actions.RequestChoiceHandler.Options(choice.Options, this)) is { } option)
                {
                    RunActions(byChoice.Cases.FirstOrDefault(c => c.Option == option)?.Actions ?? []);
                }
            }
            else if (effect.Outcomes is { Roll: { } dice } outcomes)
            {
                LastRoll = Number(dice);
                var roll = LastRoll.Value;
                RunActions(outcomes.Cases.FirstOrDefault(c => c.From <= roll && roll <= c.To)?.Actions ?? []);
            }
            else
            {
                RunActions(effect.Actions);
            }

            if (Refusal is not null || Cut is not null || Suspended)
            {
                return;
            }
        }
    }

    /// <summary>
    /// A drawn event (D-511): a target <c>chosen</c> is its first question, asked among the players the server counts;
    /// otherwise it runs on its targets and may stop at a later question. The provisional first no longer affects others
    /// (SPEC «Прогресс и победа», D-557 (4)): the event of his own run reaches him alone, he is asked about nobody.
    /// </summary>
    public void Play(ObjectDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var effect = definition.Effect!;
        var alone = Targets.IsFirst(State, UserId);
        if (effect.Target?.Selector == TargetSelector.Chosen && Asks is not null)
        {
            var options = alone ? [] : Targets.Askable(State, definition, UserId);
            if (options.Count > 0)
            {
                Suspend(SuspendedStep.Target, [.. options.Select(id => id.ToString("N"))]);
            }

            return;
        }

        var targets = Targets.ForTriggered(State, Context, definition, UserId);
        Run(effect, alone ? [.. targets.Where(t => t == UserId)] : targets);
    }

    /// <summary>
    /// The rest of <paramref name="suspended"/> with the answer <paramref name="optionId"/> (D-511): the references computed
    /// before the question come back; a target asked is checked again and skipped when no longer allowed (Н-10); a case runs
    /// from its first action; an action that asked is played again with the answer, then the ones after it.
    /// </summary>
    public void Resume(ObjectDefinition definition, SuspendedEffect suspended, string optionId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(suspended);
        var effect = definition.Effect!;

        // The provisional first goes on with the event of his own run (D-553 (3)), but it reaches him alone, as in Play
        // (D-557 (4)): a question about another player asked before he became first hits nobody
        var alone = Targets.IsFirst(State, UserId);
        if (suspended.Step == SuspendedStep.Target)
        {
            if (Guid.TryParseExact(optionId, "N", out var target) && (!alone || target == UserId)
                && Targets.Askable(State, definition, UserId).Contains(target))
            {
                Run(effect, [target]);
            }

            return;
        }

        TargetId = suspended.TargetId ?? UserId;
        if (alone && TargetId != UserId)
        {
            return;
        }

        // A player picked at an earlier question is checked again at every later one (Н-10, D-510): one who finished first
        // or became inactive since is not hit, the answer is not refused
        if (TargetId != UserId && effect.Target?.Selector == TargetSelector.Chosen && !Targets.Askable(State, definition, UserId).Contains(TargetId))
        {
            return;
        }

        LastRoll = suspended.Roll;
        LastChoice = suspended.Choice;
        if (suspended.Step == SuspendedStep.Outcome)
        {
            // The options of a branch by choice are a fixed list (ContentValidator): the answer of the snapshot is still one
            _choiceIndex = _choices.Count;
            LastChoice = optionId;
            RunActions(effect.Outcomes?.Cases.FirstOrDefault(c => c.Option == optionId)?.Actions ?? []);
            return;
        }

        RunActions(effect.Actions, Math.Max(suspended.NextAction - 1, 0));
    }

    /// <summary>Stops the effect to wait for the answer to <paramref name="options"/> (D-511): written with what it computed so far.</summary>
    private void Suspend(SuspendedStep step, IReadOnlyCollection<string> options)
    {
        var asks = Asks!;
        var due = State.Rules.Events?.AnswerTimeoutHours is { } hours ? Context.Clock.UtcNow.AddHours(hours) : (DateTimeOffset?)null;
        Emit(new EffectSuspended(
            UserId,
            Context.Ids.NewId(),
            ObjectId,
            asks.Kind,
            step,
            step == SuspendedStep.Target ? null : TargetId,
            step == SuspendedStep.Action ? _actionIndex + 1 : 0,
            asks.Depth,
            [.. options.Select(o => new Turns.ChoiceOption(o, Game: null))],
            LastRoll,
            LastChoice,
            due)
        {
            Source = asks.Source,
        });
        Suspended = true;
    }

    private void RunActions(EquatableArray<ActionSpec> actions, int from = 0)
    {
        var selfDone = _selfDone;
        _step = SuspendedStep.Action;
        for (var i = from; i < actions.Count; i++)
        {
            var action = actions[i];
            if (selfDone && action.Target == Content.ActionTarget.Self && TargetId != UserId)
            {
                continue;
            }

            _actionIndex = i;
            Actions.BaseActionRegistry.ByType[action.GetType()].Execute(action, this);
            _selfDone |= action.Target == Content.ActionTarget.Self;
            if (Refusal is not null || Cut is not null || Suspended)
            {
                return;
            }
        }
    }
}
