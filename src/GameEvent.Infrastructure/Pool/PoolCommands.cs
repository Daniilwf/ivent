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
    public const int MaxTitleLength = 200;
    public const int MaxTags = 20;
    public const int MaxTagLength = 50;
    public const int MaxNoteLength = 1000;
    public const int MaxAuthorNameLength = 64;
    public const int MaxDeletionReasonLength = 500;
    public const decimal MinHours = 0.5m;
    public const decimal MaxHours = 1000m;
    public const int MinYear = 1950;
    public const int MaxYear = 2100;
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

    /// <summary>A card as it is stored: trimmed, tags without repeats (case-insensitively), blanks as none; or why not.</summary>
    public static (GameCard? Card, string? Problem) Normalize(GameCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var title = Tidy(card.Title);
        if (title.Length is 0 or > MaxTitleLength)
        {
            return (null, $"A title is 1–{MaxTitleLength} characters.");
        }

        var tags = card.Tags.Select(t => t?.Trim() ?? "").Where(t => t.Length > 0).DistinctBy(t => t.ToUpperInvariant()).ToList();
        if (tags.Count > MaxTags || tags.Any(t => t.Length > MaxTagLength))
        {
            return (null, $"Up to {MaxTags} tags of at most {MaxTagLength} characters.");
        }

        if (card.Hours is { } hours && (hours < MinHours || hours > MaxHours || decimal.Round(hours * 2) != hours * 2))
        {
            return (null, $"Hours are {MinHours}–{MaxHours}, in half hours.");
        }

        if (card.Year is { } year && (year < MinYear || year > MaxYear))
        {
            return (null, $"A year is {MinYear}–{MaxYear}.");
        }

        var steam = string.IsNullOrWhiteSpace(card.SteamAppId) ? null : card.SteamAppId.Trim();
        if (steam is not null && (steam.Length > 12 || !steam.All(char.IsAsciiDigit)))
        {
            return (null, "A Steam app id is up to 12 digits.");
        }

        var note = string.IsNullOrWhiteSpace(card.Note) ? null : card.Note.Trim();
        var condition = string.IsNullOrWhiteSpace(card.CompletionCondition) ? null : card.CompletionCondition.Trim();
        if (note?.Length > MaxNoteLength || condition?.Length > MaxNoteLength)
        {
            return (null, $"A note and a condition are at most {MaxNoteLength} characters.");
        }

        return (new GameCard(title, [.. tags], card.Hours, card.Year, steam, card.CoverFileId, note, card.IsCoop, condition), null);
    }

    /// <summary>
    /// The same title: equal ignoring case and surrounding spaces — never twice in the pool. Alike: equal once letters and
    /// digits are all that is left («Dice Fold» and «Dice &amp; Fold»), or a letter or two apart in a long enough title.
    /// </summary>
    public static bool IsSame(string a, string b) =>
        string.Equals(Tidy(a), Tidy(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A title as stored: one Unicode form (a composed «é» equals a decomposed one) and single spaces — two titles that look
    /// alike are the same title.
    /// </summary>
    public static string Tidy(string? title) =>
        string.Join(' ', (title ?? "").Normalize(System.Text.NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
