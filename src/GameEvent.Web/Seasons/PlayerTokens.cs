using System.Globalization;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

/// <summary>
/// A player's token colour in a season (D-150, D-202): their place in the season's list of players by name. Every view
/// that shows a player's sticker — the season screen, the feed, a profile, a game page — takes <c>token</c> from here, so
/// one player has one colour everywhere in a season.
/// </summary>
public static class PlayerTokens
{
    /// <summary>
    /// Names in the order of the Russian alphabet (K1O-1, D-631): «Ё» is its own letter right after «Е» (Енот, Ёж, Заяц),
    /// not before «А» as the ordinal order of the database puts it; letters before case. Every list of players by name
    /// sorts with it.
    /// </summary>
    public static IComparer<string> NameOrder { get; } = new RussianNameComparer();

    /// <summary>The season's players in token order: by name, then by id as the database stores it.</summary>
    public static List<SeasonPlayerRecord> InTokenOrder(this IEnumerable<SeasonPlayerRecord> players) =>
        players.InTokenOrder(p => p.Name, p => p.Id);

    /// <summary>Anything that names a season player, in token order: by name, then by id as the database stores it.</summary>
    public static List<T> InTokenOrder<T>(this IEnumerable<T> players, Func<T, string> name, Func<T, Guid> id) =>
        [.. players.OrderBy(name, NameOrder).ThenBy(p => id(p).ToString("D").ToUpperInvariant(), StringComparer.Ordinal)];

    /// <summary>The token of every player of the given seasons, by the season player's id.</summary>
    public static async Task<Dictionary<Guid, int>> TokensAsync(this GameEventDbContext db, IReadOnlyCollection<Guid> seasonIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        // Only what the order needs: the rows carry JSON columns the tokens do not read
        var players = (await db.SeasonPlayers.AsNoTracking()
            .Where(p => seasonIds.Contains(p.SeasonId))
            .Select(p => new { p.Id, p.SeasonId, p.Name })
            .ToListAsync(ct))
            .InTokenOrder(p => p.Name, p => p.Id);
        return players.GroupBy(p => p.SeasonId).SelectMany(g => g.Select((p, i) => (p.Id, Token: i))).ToDictionary(x => x.Id, x => x.Token);
    }

    /// <summary>The user's token in the latest season they play (the profile's first season), or none.</summary>
    public static async Task<int?> LatestTokenAsync(this GameEventDbContext db, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var latest = await db.SeasonPlayers.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Join(db.Seasons.AsNoTracking(), p => p.SeasonId, s => s.Id, (p, s) => new { p.Id, p.SeasonId, s.CreatedAt })
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (latest is null)
        {
            return null;
        }

        var tokens = await db.TokensAsync([latest.SeasonId], ct);
        return tokens.TryGetValue(latest.Id, out var token) ? token : null;
    }
}

/// <summary>
/// The Russian alphabet with «Ё» between «Е» and «Ж»: letters compare without case first, then by the culture, then
/// ordinally, so the order is total. The culture's own collation would put «Ёж» before «Енот» (Ё as a variant of Е).
/// </summary>
internal sealed class RussianNameComparer : IComparer<string>
{
    private static readonly StringComparer s_culture = StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), ignoreCase: false);

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var byLetter = Rank(x[i]).CompareTo(Rank(y[i]));
            if (byLetter != 0)
            {
                return byLetter;
            }
        }

        var byLength = x.Length.CompareTo(y.Length);
        if (byLength != 0)
        {
            return byLength;
        }

        var byCulture = s_culture.Compare(x, y);
        return byCulture != 0 ? byCulture : string.CompareOrdinal(x, y);
    }

    /// <summary>A letter's place without case; «ё» goes right after «е», before «ж».</summary>
    private static int Rank(char c)
    {
        var lower = char.ToLowerInvariant(c);
        return lower == 'ё' ? ('е' * 2) + 1 : lower * 2;
    }
}
