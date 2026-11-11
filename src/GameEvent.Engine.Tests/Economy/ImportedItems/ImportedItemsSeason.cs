using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// The season the imported items of the old pack are played in (content/imports/old-items, docs/PROGRESS.md «Этап 4:
/// очередь импорта»): the repository's content files as the admin publishes them, three players with coins to lose, the
/// pinned test ruleset (a linear map of 60 cells, normal difficulty d4, dice by hours / 3).
/// </summary>
internal static class ImportedItemsSeason
{
    public static Scenario New(int seed = 42, int? mapLength = null)
    {
        var s = EconomyScenario.New(seed);
        if (mapLength is { } length)
        {
            s.WithMapLength(length);
        }

        s.WithCategory("Horror")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror")
            .WithPlayers("Вася", "Петя", "Коля");
        return s.WithContent(EconomyScenario.RepositoryPack())
            .WithCoins("Вася", 20).WithCoins("Петя", 20).WithCoins("Коля", 20);
    }

    /// <summary>
    /// On a map of 2 cells Петя finished first (frozen, out of the game) and Коля second (his position fixed, only points
    /// and coins reach him, D-424); Вася plays on.
    /// </summary>
    public static Scenario WithFinishers()
    {
        var s = New(mapLength: 2);
        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(1, 1).Complete("Петя");
        s.RollTitle("Коля", "Dead Space").Start("Коля").NextRandom(1, 1, 1).Complete("Коля");
        return s.Player("Коля").Finish?.Order == 2 ? s : throw new InvalidOperationException("Коля did not finish second.");
    }

    /// <summary>The instance id of the object as a player's answer names it.</summary>
    public static string Answer(Guid instance) => instance.ToString("N");

    /// <summary>The player stands on <paramref name="cell"/> after a move of the admin.</summary>
    public static Scenario At(this Scenario s, string player, string cell)
    {
        s.Act(new Engine.Players.AdjustPlayer(s.PlayerId(player), Comment: "Позиция для теста", CellId: cell));
        return s.Last.IsAccepted ? s : throw new InvalidOperationException($"Moving {player} was rejected: {s.Last.Rejection}");
    }
}
