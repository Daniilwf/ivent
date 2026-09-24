using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rolls;

namespace GameEvent.Engine.Turns;

/// <summary>What the player is choosing. Stage 1 has games only; branches, targets and outcomes come later (K-5).</summary>
public enum ChoiceKind
{
    /// <summary>One of several rolled games (<c>roll.choiceCount</c> &gt; 1, D-06).</summary>
    Game,
}

/// <summary>
/// One option of a choice. <see cref="Id"/> is what the player sends back; the payload field of the choice's kind is
/// set (a later kind adds its own nullable field). A game option's id is the game id in the <c>N</c> format.
/// </summary>
public sealed record ChoiceOption(string Id, RollOffer? Game);

/// <summary>
/// The turn waits for the player's decision (SPEC «Игровой цикл»: one general state, kept on the server, so a closed
/// tab breaks nothing). While a choice is pending the player's other turn commands are rejected.
/// </summary>
public sealed record PendingChoice(Guid ChoiceId, ChoiceKind Kind, EquatableArray<ChoiceOption> Options);

/// <summary>The player picks <see cref="OptionId"/> of the pending choice <see cref="ChoiceId"/>.</summary>
public sealed record MakeChoice(Guid PlayerId, Guid ChoiceId, string OptionId) : ICommand;

/// <summary>
/// The player picked an option. For a game choice the picked game becomes the offer, the others are freed, and a
/// <see cref="Runs.RunStarted"/> of the same command starts it (D-91).
/// </summary>
[EventType("choice-made")]
public sealed record ChoiceMade(Guid PlayerId, Guid ChoiceId, string OptionId) : IGameEvent;

/// <summary>The admin discarded a pending choice (<see cref="Players.AdjustPlayer.DiscardOffer"/>); its games are free again.</summary>
[EventType("choice-discarded")]
public sealed record ChoiceDiscarded(Guid PlayerId, Guid ChoiceId) : IGameEvent;
