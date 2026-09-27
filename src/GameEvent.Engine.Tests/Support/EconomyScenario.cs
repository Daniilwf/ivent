using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Tests.Support;

/// <summary>
/// The economy in scenarios (stage 4): the flags on, content published, objects given by the admin, items used, the shop and
/// bets — each a command of the engine, so a test reads like a game.
/// </summary>
public static class EconomyScenario
{
    /// <summary>The pinned test ruleset with items, the shop and bets on.</summary>
    public static Ruleset WithEconomy(this Ruleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        return ruleset with { Features = ruleset.Features with { Items = true, Shop = true, Bets = true } };
    }

    /// <summary>A scenario on the pinned ruleset with the economy on.</summary>
    public static Scenario New(int seed = 42) => Scenario.New(TestRuleset.Create().WithEconomy(), seed);

    /// <summary>The objects of the items and effects examples of docs/CONTENT.md, by id.</summary>
    public static IReadOnlyDictionary<string, ObjectDefinition> Examples() => Content.ContentExamplesTests.ExampleObjects();

    /// <summary>A pack of the CONTENT.md examples named by <paramref name="ids"/> (all items and effects when none).</summary>
    public static ContentPack ExamplePack(params string[] ids)
    {
        var examples = Examples();
        var chosen = ids.Length == 0
            ? examples.Values.Where(o => o.Kind is ObjectKind.Item or ObjectKind.Effect or ObjectKind.SpecialRoll)
            : ids.Select(id => examples[id]);
        return new ContentPack { Objects = [.. chosen.OrderBy(o => o.Id, StringComparer.Ordinal)] };
    }

    /// <summary>The content files of the repository: /content/items and /content/wheels.</summary>
    public static ContentPack RepositoryPack()
    {
        var root = Path.Combine(RepositoryPaths.Root(), "content");
        static IEnumerable<(string, string)> Read(string dir) =>
            Directory.EnumerateFiles(dir, "*.json").Select(f => (Path.GetFileName(f), File.ReadAllText(f)));
        return ContentFiles.Pack(Read(Path.Combine(root, "items")), Read(Path.Combine(root, "wheels")));
    }

    /// <summary>The admin publishes <paramref name="pack"/>; it must be accepted.</summary>
    public static Scenario WithContent(this Scenario s, ContentPack pack)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Act(new PublishContent(pack, "Контент сезона"));
        return s.Last.IsAccepted ? s : throw new InvalidOperationException($"Publishing content was rejected: {s.Last.Rejection}");
    }

    /// <summary>The admin gives <paramref name="player"/> an object; returns its instance id.</summary>
    public static Guid Give(this Scenario s, string player, string objectId)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Act(new AdjustInventory(s.PlayerId(player), objectId, null, "Выдано для теста"));
        return s.Last.IsAccepted
            ? s.LastEvents<ObjectGiven>().Single().Item.InstanceId
            : throw new InvalidOperationException($"Giving {objectId} to {player} was rejected: {s.Last.Rejection}");
    }

    /// <summary>The player uses an item (the result is in <see cref="Scenario.Last"/>).</summary>
    public static Scenario Use(this Scenario s, string player, Guid instance, string? target = null, params string[] choices)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Act(new UseItem(s.PlayerId(player), instance, target is null ? null : s.PlayerId(target), [.. choices]));
    }

    /// <summary>The player uses the item <paramref name="objectId"/> they hold; it must be accepted.</summary>
    public static Scenario Used(this Scenario s, string player, string objectId, string? target = null, params string[] choices)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Use(player, s.Held(player, objectId).InstanceId, target, choices);
        return s.Last.IsAccepted ? s : throw new InvalidOperationException($"Using {objectId} was rejected: {s.Last.Rejection}");
    }

    /// <summary>The object <paramref name="objectId"/> the player holds (the first one).</summary>
    public static InventoryObject Held(this Scenario s, string player, string objectId)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Player(player).Wallet.Inventory.First(o => o.ObjectId == objectId);
    }

    /// <summary>The ids of the objects the player holds, in order.</summary>
    public static IReadOnlyList<string> Inventory(this Scenario s, string player)
    {
        ArgumentNullException.ThrowIfNull(s);
        return [.. s.Player(player).Wallet.Inventory.Select(o => o.ObjectId)];
    }

    /// <summary>The admin sets the player's coins (a correction with a comment).</summary>
    public static Scenario WithCoins(this Scenario s, string player, int coins)
    {
        ArgumentNullException.ThrowIfNull(s);
        var delta = coins - s.Player(player).Coins;
        if (delta != 0)
        {
            s.Act(new Engine.Players.AdjustPlayer(s.PlayerId(player), CoinsDelta: delta, Comment: "Монетки для теста"));
            if (!s.Last.IsAccepted)
            {
                throw new InvalidOperationException($"Setting coins was rejected: {s.Last.Rejection}");
            }
        }

        return s;
    }

    /// <summary>The admin sets the player's points.</summary>
    public static Scenario WithPoints(this Scenario s, string player, int points)
    {
        ArgumentNullException.ThrowIfNull(s);
        var delta = points - s.Player(player).Points;
        if (delta != 0)
        {
            s.Act(new Engine.Players.AdjustPlayer(s.PlayerId(player), PointsDelta: delta, Comment: "Очки для теста"));
            if (!s.Last.IsAccepted)
            {
                throw new InvalidOperationException($"Setting points was rejected: {s.Last.Rejection}");
            }
        }

        return s;
    }

    public static Scenario RollShop(this Scenario s, string player)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Act(new RollShop(s.PlayerId(player)));
    }

    public static Scenario Buy(this Scenario s, string player, int lot)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Act(new BuyLot(s.PlayerId(player), lot));
    }

    public static Scenario Bet(this Scenario s, string player, string on, int days, int stake)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Act(new PlaceBet(s.PlayerId(player), s.PlayerId(on), days, stake));
    }

    public static Scenario FireTimers(this Scenario s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Act(new FireTimers());
    }

    /// <summary>The events of the last command, of any of the given types, as their type names — to read the order.</summary>
    public static IReadOnlyList<string> LastTypes(this Scenario s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return [.. s.Last.Events.Select(e => EventCatalog.Describe(e.GetType()).Name)];
    }
}
