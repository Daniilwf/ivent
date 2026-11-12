using System.Globalization;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;

namespace GameEvent.Infrastructure.Pool;

/// <summary>
/// A player or the admin adds a game (D-119). The queue gives it an id, so a repeated request is the same command.
/// <see cref="Force"/> — added although the pool has a similar title (the site warned, SPEC «Дубли»);
/// <see cref="AuthorName"/> — the author as the imported table names them (F1, D-125).
/// </summary>
public sealed record AddGame(GameCard Card, Guid? AuthorId, bool Force, string? AuthorName = null) : Queue.IGlobalCommand;

/// <summary>The admin changes a game's card.</summary>
public sealed record ChangeGame(Guid GameId, GameCard Card, bool Force) : Queue.IGlobalCommand;

/// <summary>The admin takes a game out of the pool (soft), saying why (D-208): the reason is shown to whoever adds it again.</summary>
public sealed record DeleteGame(Guid GameId, string Reason) : Queue.IGlobalCommand;

public sealed record RestoreGame(Guid GameId) : Queue.IGlobalCommand;

/// <summary>The admin puts a category on the wheel or changes its weight.</summary>
public sealed record SetCategory(string Name, int Weight) : Queue.IGlobalCommand;

public sealed record RemoveCategory(string Name) : Queue.IGlobalCommand;

/// <summary>The pool's rules (D-119): what a card may hold and which titles are the same or alike.</summary>
public static class PoolRules
{
    public const int MaxTitleLength = GameCards.MaxTitleLength;
    public const int MaxTags = GameCards.MaxTags;
    public const int MaxTagLength = GameCards.MaxTagLength;
    public const int MaxNoteLength = GameCards.MaxNoteLength;
    public const int MaxAuthorNameLength = 64;
    public const int MaxDeletionReasonLength = 500;
    public const decimal MinHours = GameCards.MinHours;
    public const decimal MaxHours = GameCards.MaxHours;
    public const int MinYear = GameCards.MinYear;
    public const int MaxYear = GameCards.MaxYear;
    public const int MaxCategoryWeight = 1000;

    public const string CardInvalid = "pool.cardInvalid";
    public const string Duplicate = "pool.duplicate";
    public const string Similar = "pool.similar";
    public const string Unknown = "pool.unknown";
    public const string Deleted = "pool.deleted";

    /// <summary>The title is a game the admin took out of the pool (D-208): only the admin's restore brings it back.</summary>
    public const string Removed = "pool.removed";
    public const string ReasonInvalid = "pool.reasonInvalid";
    public const string NotDeleted = "pool.notDeleted";
    public const string CoverUnknown = "pool.coverUnknown";
    public const string NothingToChange = "pool.nothingToChange";
    public const string CategoryInvalid = "pool.categoryInvalid";
    public const string CategoryUnknown = "pool.categoryUnknown";

    /// <summary>A card as it is stored, or why not: the engine's card rules, shared with the suggestion box (D-579).</summary>
    public static (GameCard? Card, string? Problem) Normalize(GameCard card) => GameCards.Normalize(card);

    /// <summary>
    /// The same title: equal ignoring case and surrounding spaces — never twice in the pool. Alike: equal once letters and
    /// digits are all that is left («Dice Fold» and «Dice &amp; Fold»), or a letter or two apart in a long enough title.
    /// </summary>
    public static bool IsSame(string a, string b) =>
        string.Equals(Tidy(a), Tidy(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A title as stored (<see cref="GameCards.Tidy"/>).</summary>
    public static string Tidy(string? title) => GameCards.Tidy(title);

    public static bool IsAlike(string a, string b)
    {
        var (x, y) = (Key(a), Key(b));
        if (x.Length == 0 || y.Length == 0)
        {
            return false;
        }

        if (x == y)
        {
            return true;
        }

        var allowed = Math.Min(x.Length, y.Length) switch
        {
            >= 10 => 2,
            >= 5 => 1,
            _ => 0,
        };
        return allowed > 0 && Math.Abs(x.Length - y.Length) <= allowed && Distance(x, y) <= allowed;
    }

    /// <summary>Letters and digits of a title, lower case: the key two titles are compared by.</summary>
    public static string Key(string title) =>
        new([.. Tidy(title).ToLower(CultureInfo.InvariantCulture).Where(char.IsLetterOrDigit)]);

    /// <summary>A deletion's reason as stored (trimmed), or none when it is empty or too long (D-208).</summary>
    public static string? DeletionReason(string? reason) =>
        reason?.Trim() is { Length: > 0 and <= MaxDeletionReasonLength } trimmed ? trimmed : null;

    public static Rejection? CheckCategory(string name, int weight) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxTagLength
            ? new Rejection(CategoryInvalid, $"A category is 1–{MaxTagLength} characters.")
            : weight is < 1 or > MaxCategoryWeight
                ? new Rejection(CategoryInvalid, $"A weight is 1–{MaxCategoryWeight}.")
                : null;

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
