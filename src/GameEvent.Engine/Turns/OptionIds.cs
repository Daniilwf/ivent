using System.Globalization;

namespace GameEvent.Engine.Turns;

/// <summary>
/// What an option's id may be (D-822 (2), D-827): the text an answer sends back as it is — a game id, a player id, a cell,
/// the text of a list or a category's name. One rule for the site's check of an answer and for the content check of a
/// list and of a category, so an option offered is always one a player can answer: 1–<see cref="MaxLength"/> characters,
/// no control or format characters, no angle brackets (no markup, the rule of stage 1), no blanks around. Any other
/// punctuation passes («Point &amp; Click», «Action/Adventure»): the engine compares the answer with the options offered.
/// </summary>
public static class OptionIds
{
    public const int MaxLength = 64;

    public static bool IsValid(string? id) =>
        id is { Length: > 0 and <= MaxLength }
        && !char.IsWhiteSpace(id[0])
        && !char.IsWhiteSpace(id[^1])
        && !id.Any(c => c is '<' or '>' || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format);
}
