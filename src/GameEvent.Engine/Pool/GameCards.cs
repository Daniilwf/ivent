namespace GameEvent.Engine.Pool;

/// <summary>
/// What a game's card may hold (D-119): the pool's rules, shared by the pool's commands and by a suggested game (D-579), so
/// a card the suggestion box accepts is one the pool accepts too.
/// </summary>
public static class GameCards
{
    public const int MaxTitleLength = 200;
    public const int MaxTags = 20;
    public const int MaxTagLength = 50;
    public const int MaxNoteLength = 1000;
    public const decimal MinHours = 0.5m;
    public const decimal MaxHours = 1000m;
    public const int MinYear = 1950;
    public const int MaxYear = 2100;
    public const int MaxSteamAppIdLength = 12;

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
        if (steam is not null && (steam.Length > MaxSteamAppIdLength || !steam.All(char.IsAsciiDigit)))
        {
            return (null, $"A Steam app id is up to {MaxSteamAppIdLength} digits.");
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
    /// A title as stored: one Unicode form (a composed «é» equals a decomposed one) and single spaces — two titles that look
    /// alike are the same title.
    /// </summary>
    public static string Tidy(string? title) =>
        string.Join(' ', (title ?? "").Normalize(System.Text.NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
