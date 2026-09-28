using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;

namespace GameEvent.Engine.Runs;

/// <summary>
/// «Реролл по желанию» (D-206, D-325): a tech reroll with the reason «wish» of a game tagged with one of
/// <c>roll.wishRerollTags</c> as the run fixed them at the roll (<see cref="RunSnapshot.WishRerollTags"/>), when the roll's own
/// filters imposed no genre (<see cref="RunSnapshot.ImposedTags"/>). The window is the tech reroll's, checked with it. Tags
/// compare ignoring case, like the wheel's.
/// </summary>
public static class WishRerolls
{
    /// <summary>Whether <paramref name="game"/>, rolled with <paramref name="snapshot"/>, may be given up by wish, the window aside.</summary>
    public static bool Allowed(RunSnapshot snapshot, Game? game)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ImposedTags.Count == 0 && Listed(snapshot, game);
    }

    internal static Decision? Refusal(RunState run, IPoolView pool)
    {
        var game = pool.Games.FirstOrDefault(g => g.Id == run.GameId);
        return !Listed(run.Snapshot, game)
            ? Decision.Reject(RejectionCodes.WishRerollNotListed, "The game has none of the tags a wish reroll is allowed for.")
            : run.Snapshot.ImposedTags.Count > 0
                ? Decision.Reject(RejectionCodes.WishRerollImposed, $"The roll imposed the genre ({string.Join(", ", run.Snapshot.ImposedTags)}).")
                : null;
    }

    private static bool Listed(RunSnapshot snapshot, Game? game) =>
        game is not null && snapshot.WishRerollTags.Any(t => game.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
}
