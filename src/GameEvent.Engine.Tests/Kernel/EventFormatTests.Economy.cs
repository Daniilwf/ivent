using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>The frozen formats of the economy's events (stage 4, D-400…).</summary>
public partial class EventFormatTests
{
    private static readonly Guid s_object = Guid.Parse("60000000-0000-0000-0000-000000000001");

    private static InventoryObject Curse() =>
        new(s_object, "curse-effect", ObjectKind.Effect, 3, Hostile: true, FromPlayerId: s_other, UsesLeft: 1);

    private static InventoryObject Forced() =>
        new(s_object, "forced-genre", ObjectKind.SpecialRoll, 2, Params: new ContentParamDictionary([new("tag", "Horror")]), ExpiresAt: s_at, Notes: ["Помечен"]);

    private static TheoryData<string, IGameEvent, int, string> WithEconomy(TheoryData<string, IGameEvent, int, string> data)
    {
        foreach (var (type, sample, json) in EconomySamples())
        {
            data.Add(type, sample, 1, json);
        }

        return data;
    }

    private static IEnumerable<(string Type, IGameEvent Sample, string Json)> EconomySamples() =>
    [
        ("content-published", new ContentPublished(2, new ContentPack(), "Весна"), """"{"version":2,"pack":{"objects":[],"wheels":[]},"comment":"Весна"}""""),
        ("object-given", new ObjectGiven(s_player, Curse(), ObjectSource.Effect, s_other), """"{"playerId":"10000000-0000-0000-0000-000000000001","item":{"instanceId":"60000000-0000-0000-0000-000000000001","objectId":"curse-effect","kind":"effect","acquired":3,"hostile":true,"fromPlayerId":"10000000-0000-0000-0000-000000000002","params":null,"usesLeft":1,"runsLeft":null,"expiresAt":null,"notes":[]},"source":"effect","fromPlayerId":"10000000-0000-0000-0000-000000000002"}""""),
        ("object-removed", new ObjectRemoved(s_player, s_object, "orange", ObjectRemoval.Used), """"{"playerId":"10000000-0000-0000-0000-000000000001","instanceId":"60000000-0000-0000-0000-000000000001","objectId":"orange","reason":"used"}""""),
        ("object-transferred", new ObjectTransferred(s_player, s_other, s_object, "orange"), """"{"playerId":"10000000-0000-0000-0000-000000000001","toPlayerId":"10000000-0000-0000-0000-000000000002","instanceId":"60000000-0000-0000-0000-000000000001","objectId":"orange"}""""),
        ("object-changed", new ObjectChanged(s_player, Curse() with { UsesLeft = 2 }), """"{"playerId":"10000000-0000-0000-0000-000000000001","item":{"instanceId":"60000000-0000-0000-0000-000000000001","objectId":"curse-effect","kind":"effect","acquired":3,"hostile":true,"fromPlayerId":"10000000-0000-0000-0000-000000000002","params":null,"usesLeft":2,"runsLeft":null,"expiresAt":null,"notes":[]}}""""),
        ("object-lost", new ObjectLost(s_player, "orange"), """"{"playerId":"10000000-0000-0000-0000-000000000001","objectId":"orange"}""""),
        ("item-used", new ItemUsed(s_player, s_object, "dirty-trick", [s_other], ["Horror"]), """"{"playerId":"10000000-0000-0000-0000-000000000001","instanceId":"60000000-0000-0000-0000-000000000001","objectId":"dirty-trick","targets":["10000000-0000-0000-0000-000000000002"],"choices":["Horror"]}""""),
        ("effect-triggered", new EffectTriggered(s_player, s_object, "curse-effect", Trigger.BeforeDice), """"{"playerId":"10000000-0000-0000-0000-000000000001","instanceId":"60000000-0000-0000-0000-000000000001","objectId":"curse-effect","trigger":"beforeDice"}""""),
        ("effect-rolled", new EffectRolled(s_player, "bird-thief", [new Die(4, 3)], 3), """"{"playerId":"10000000-0000-0000-0000-000000000001","objectId":"bird-thief","dice":[{"sides":4,"value":3}],"total":3}""""),
        ("hostile-intercepted", new HostileIntercepted(s_player, s_object, "curse", s_other), """"{"playerId":"10000000-0000-0000-0000-000000000001","instanceId":"60000000-0000-0000-0000-000000000001","objectId":"curse","fromPlayerId":"10000000-0000-0000-0000-000000000002"}""""),
        ("hostile-received", new HostileReceived(s_player, s_other, "curse"), """"{"playerId":"10000000-0000-0000-0000-000000000001","fromPlayerId":"10000000-0000-0000-0000-000000000002","objectId":"curse"}""""),
        ("wheel-spun", new WheelSpun(s_player, "lootbox", "orange"), """"{"playerId":"10000000-0000-0000-0000-000000000001","wheelId":"lootbox","objectId":"orange"}""""),
        ("next-roll-modified", new NextRollModified(s_player, new RollModifier("short-game", new GameFilterSpec { MaxHours = 10 }, null)), """"{"playerId":"10000000-0000-0000-0000-000000000001","modifier":{"objectId":"short-game","filter":{"tags":null,"maxHours":10,"minHours":null,"releaseYearBefore":null},"choiceCount":null,"hostile":false}}""""),
        ("roll-modifiers-applied", new RollModifiersApplied(s_player, [new RollModifier("pick-of-three", null, 3, Hostile: true)]), """"{"playerId":"10000000-0000-0000-0000-000000000001","modifiers":[{"objectId":"pick-of-three","filter":null,"choiceCount":3,"hostile":true}]}""""),
        ("next-dice-modified", new NextDiceModified(s_player, new DiceModifier("lucky-die", DiceStage.Add, ContentValue.TryParse("1d6")!)), """"{"playerId":"10000000-0000-0000-0000-000000000001","modifier":{"objectId":"lucky-die","stage":"add","value":"1d6","hostile":false}}""""),
        ("inventory-adjusted", new InventoryAdjusted(s_player, "Компенсация"), """"{"playerId":"10000000-0000-0000-0000-000000000001","comment":"Компенсация"}""""),
        ("run-dice-modified", new RunDiceModified(s_run, s_player, RunDiceMods.None with { Sides = 8, ExtraDice = [new Die(8, 5)], Added = -2, Multiplier = 2, Min = 1, Max = 30, Sources = ["curse-effect"] }, [new Die(6, 2)], SpentNext: 1), """"{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","mods":{"sides":8,"extraDice":[{"sides":8,"value":5}],"added":-2,"multiplier":2,"min":1,"max":30,"sources":["curse-effect"]},"rolled":[{"sides":6,"value":2}],"spentNext":1}""""),
        ("run-dice-rerolled", new RunDiceRerolled(s_run, s_player, "reroll-dice", [new Die(4, 4)], [], [new Die(4, 1)]), """"{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","objectId":"reroll-dice","dice":[{"sides":4,"value":4}],"challengeDice":[],"extraDice":[{"sides":4,"value":1}]}""""),
        ("shop-rolled", new ShopRolled(s_player, [new ShopLot("orange", Rarity.Common, 10), new ShopLot("curse", Rarity.Legendary, 45, Sold: true)], s_at, ShopPayment.Coins, 5), """"{"playerId":"10000000-0000-0000-0000-000000000001","lots":[{"objectId":"orange","rarity":"common","price":10,"sold":false},{"objectId":"curse","rarity":"legendary","price":45,"sold":true}],"expiresAt":"2026-10-01T12:30:00+00:00","payment":"coins","price":5}""""),
        ("lot-bought", new LotBought(s_player, 1, Curse() with { Hostile = false, FromPlayerId = null }, 45), """"{"playerId":"10000000-0000-0000-0000-000000000001","lot":1,"item":{"instanceId":"60000000-0000-0000-0000-000000000001","objectId":"curse-effect","kind":"effect","acquired":3,"hostile":false,"fromPlayerId":null,"params":null,"usesLeft":1,"runsLeft":null,"expiresAt":null,"notes":[]},"price":45}""""),
        ("shop-offer-expired", new ShopOfferExpired(s_player), """"{"playerId":"10000000-0000-0000-0000-000000000001"}""""),
        ("shop-price-restarted", new ShopPriceRestarted(s_player), """"{"playerId":"10000000-0000-0000-0000-000000000001"}""""),
        ("bet-placed", new BetPlaced(s_player, new Bet(s_object, s_other, s_run, s_at, s_at.AddDays(3), 5, 1.2m)), """"{"playerId":"10000000-0000-0000-0000-000000000001","bet":{"betId":"60000000-0000-0000-0000-000000000001","onPlayerId":"10000000-0000-0000-0000-000000000002","runId":"00000000-0000-0000-0000-000000000001","placedAt":"2026-10-01T12:30:00+00:00","deadline":"2026-10-04T12:30:00+00:00","stake":5,"multiplier":1.2,"status":"open","payout":0}}""""),
        ("bet-settled", new BetSettled(s_player, s_object, BetStatus.Won, 6), """"{"playerId":"10000000-0000-0000-0000-000000000001","betId":"60000000-0000-0000-0000-000000000001","status":"won","payout":6}""""),
    ];

    /// <summary>Second shapes of events above: an object given by the admin with parameters and notes, mods with only an addition.</summary>
    public static TheoryData<string, IGameEvent, string> EconomyVariants()
    {
        var data = new TheoryData<string, IGameEvent, string>();
        foreach (var (type, sample, json) in Variants())
        {
            data.Add(type, sample, json);
        }

        return data;
    }

    private static IEnumerable<(string Type, IGameEvent Sample, string Json)> Variants() =>
    [
        ("object-given", new ObjectGiven(s_player, Forced(), ObjectSource.Admin), """"{"playerId":"10000000-0000-0000-0000-000000000001","item":{"instanceId":"60000000-0000-0000-0000-000000000001","objectId":"forced-genre","kind":"specialRoll","acquired":2,"hostile":false,"fromPlayerId":null,"params":{"tag":"Horror"},"usesLeft":null,"runsLeft":null,"expiresAt":"2026-10-01T12:30:00+00:00","notes":["Помечен"]},"source":"admin","fromPlayerId":null}""""),
        ("run-dice-modified", new RunDiceModified(s_run, s_player, RunDiceMods.None with { Added = 4, Sources = ["lucky-die"] }, [new Die(6, 4)]), """"{"runId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","mods":{"added":4,"multiplier":1,"sources":["lucky-die"]},"rolled":[{"sides":6,"value":4}]}""""),
    ];

    [Fact]
    public void Manual_effect_created_v1_reads_as_it_was_and_v2_carries_the_text_of_an_object()
    {
        var v1 = new StoredEvent(
            "manual-effect-created",
            1,
            """{"effectId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","drawEvent":"bad","source":"drop","runId":null}""");
        Assert.Equal(new Engine.Effects.ManualEffectCreated(s_run, s_player, Engine.Rulesets.EventKind.Bad, Engine.Effects.ManualEffectSource.Drop, null), EventCodec.Decode(v1));

        var text = new Engine.Effects.ManualEffectCreated(s_run, s_player, null, Engine.Effects.ManualEffectSource.Item, null) { ObjectId = "gift-of-fate" };
        Assert.Equal(
            new StoredEvent("manual-effect-created", 2, """{"effectId":"00000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","drawEvent":null,"source":"item","runId":null,"objectId":"gift-of-fate"}"""),
            EventCodec.Encode(text));
        Assert.Equal(text, EventCodec.Decode(EventCodec.Encode(text)));
    }

    [Theory]
    [MemberData(nameof(EconomyVariants))]
    public void Economy_variant_is_stored_in_the_frozen_format(string type, IGameEvent sample, string json)
    {
        Assert.Equal(new StoredEvent(type, 1, json), EventCodec.Encode(sample));
        Assert.Equal(sample, EventCodec.Decode(new StoredEvent(type, 1, json)));
    }
}
