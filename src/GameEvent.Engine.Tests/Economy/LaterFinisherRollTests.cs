using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// EC4 (D-425): hostile changes of the game's choice that other players put on a player before he finished not first
/// are ignored at his rolls after the finish; dice changes still apply (after the finish dice give only points), and so
/// do his own changes and the same hostile changes on a player who has not finished.
/// </summary>
public class LaterFinisherRollTests
{
    private static readonly int[] s_seeds = [1, 2, 3, 4, 5, 6, 7, 8];

    private static readonly Dictionary<string, string> s_genres = new()
    {
        ["Silent Hill"] = "Horror",
        ["Alan Wake"] = "Horror",
        ["Dead Space"] = "Horror",
        ["Outlast"] = "Horror",
        ["Persona"] = "RPG",
        ["Fallout"] = "RPG",
        ["Gothic"] = "RPG",
        ["Witcher"] = "RPG",
    };

    private static ObjectDefinition Item(string id, EffectSpec effect, bool hostile) =>
        new()
        {
            Id = id,
            Kind = ObjectKind.Item,
            Name = "Проверка",
            Description = "Предмет для проверки финиша.",
            Rarity = Rarity.Common,
            Window = UseWindow.Anytime,
            Hostile = hostile,
            Stackable = true,
            Effect = effect,
        };

    // A hostile change of the next roll put straight on the target (a pending roll change, not a special roll)
    private static readonly ObjectDefinition s_rpgOnly = Item(
        "rpg-only",
        new EffectSpec
        {
            Target = new TargetSpec { Selector = TargetSelector.Chosen, ExcludeSelf = true },
            Actions = [new ModifyNextRollAction { Filter = new GameFilterSpec { Tags = ["RPG"] } }],
        },
        hostile: true);

    // The player's own wish for his next roll
    private static readonly ObjectDefinition s_myRpg = Item(
        "my-rpg",
        new EffectSpec { Actions = [new ModifyNextRollAction { Filter = new GameFilterSpec { Tags = ["RPG"] } }] },
        hostile: false);

    // The player's own special roll that changes his game
    private static readonly ObjectDefinition s_myRpgRoll = new()
    {
        Id = "my-rpg-roll",
        Kind = ObjectKind.SpecialRoll,
        Name = "Свой жанр",
        Description = "Следующая игра — RPG.",
        Rarity = Rarity.Common,
        Effect = new EffectSpec
        {
            Trigger = Trigger.BeforeRoll,
            Duration = new DurationSpec { Uses = 1 },
            Actions = [new ModifyNextRollAction { Filter = new GameFilterSpec { Tags = ["RPG"] } }],
        },
    };

    // A curse that waits for a long run: it does not fire on the 6-hour finishing run, only on a later one of 9 hours
    private static readonly ObjectDefinition s_slowCurseEffect = new()
    {
        Id = "slow-curse-effect",
        Kind = ObjectKind.Effect,
        Name = "Долгое проклятие",
        Description = "Бросок за длинную игру −1d6, минимум 1.",
        Rarity = Rarity.Common,
        Hostile = true,
        Effect = new EffectSpec
        {
            Trigger = Trigger.BeforeDice,
            Condition = new ConditionSpec { Game = new GameFilterSpec { MinHours = 9 } },
            Duration = new DurationSpec { Uses = 1 },
            Actions =
            [
                new ModifyDiceAction { When = DiceWhen.Current, Stage = DiceStage.Add, Value = ContentValue.TryParse("-1d6")! },
                new ModifyDiceAction { When = DiceWhen.Current, Stage = DiceStage.Min, Value = ContentValue.Of(1) },
            ],
        },
    };

    private static readonly ObjectDefinition s_slowCurse = Item(
        "slow-curse",
        new EffectSpec
        {
            Target = new TargetSpec { Selector = TargetSelector.Chosen, ExcludeSelf = true },
            Actions = [new GiveObjectAction { ObjectId = "slow-curse-effect" }],
        },
        hostile: true);

    private static Scenario WithFixtures(Scenario s)
    {
        var pack = EconomyScenario.RepositoryPack();
        return s.WithContent(pack with { Objects = [.. pack.Objects, s_rpgOnly, s_myRpg, s_myRpgRoll, s_slowCurseEffect, s_slowCurse] });
    }

    /// <summary>
    /// Horror and RPG, all games 6 hours; Петя finishes first when the map is short, Коля (10 points, more than Вася) is
    /// playing his next run, which finishes him second on a short map and not on a long one.
    /// </summary>
    private static Scenario KolyaPlaying(int seed, bool shortMap)
    {
        var s = EconomyScenario.New(seed).WithCategory("Horror").WithCategory("RPG");
        foreach (var (title, genre) in s_genres)
        {
            s.WithGame(title, 6, genre);
        }

        s.WithMapLength(shortMap ? 2 : 30).WithPlayers("Вася", "Петя", "Коля");
        WithFixtures(s);
        s.Roll("Петя").Start("Петя").NextRandom(1, 1).Complete("Петя");
        s.Roll("Коля").Start("Коля");
        return s.WithPoints("Коля", 10);
    }

    private static void HitKolya(Scenario s, string hostile)
    {
        s.Give("Вася", hostile);
        if (hostile == "dirty-trick")
        {
            s.Used("Вася", "dirty-trick", "Коля", "RPG");
        }
        else
        {
            s.Used("Вася", hostile, target: "Коля");
        }
    }

    private static string GenreOfOffer(Scenario s, string player) => s_genres[s.GameTitle(s.Player(player).Offer!.GameId)];

    [Theory]
    [InlineData("dirty-trick")]
    [InlineData("rpg-only")]
    public void A_hostile_genre_from_before_the_finish_is_ignored_at_the_rolls_after_it(string hostile)
    {
        var horror = 0;
        foreach (var seed in s_seeds)
        {
            var s = KolyaPlaying(seed, shortMap: true);
            HitKolya(s, hostile);
            s.NextRandom(1, 1).Complete("Коля");
            Assert.Equal(2, s.Player("Коля").Finish!.Order);

            s.Roll("Коля");

            Assert.True(s.Last.IsAccepted);
            Assert.DoesNotContain(s.LastEvents<EffectTriggered>(), e => e.ObjectId == "forced-genre");
            Assert.Empty(s.Player("Коля").Wallet.CurrentRoll);
            Assert.Empty(s.Player("Коля").Wallet.NextRoll);
            horror += GenreOfOffer(s, "Коля") == "Horror" ? 1 : 0;
        }

        // A normal roll: over the seeds it lands on Horror too
        Assert.True(horror > 0, "Every roll after the finish still offered the imposed genre.");
    }

    [Theory]
    [InlineData("dirty-trick")]
    [InlineData("rpg-only")]
    public void The_same_hostile_genre_still_applies_to_a_player_who_has_not_finished(string hostile)
    {
        foreach (var seed in s_seeds)
        {
            var s = KolyaPlaying(seed, shortMap: false);
            HitKolya(s, hostile);
            s.NextRandom(1, 1).Complete("Коля");
            Assert.Null(s.Player("Коля").Finish);

            s.Roll("Коля");

            Assert.Equal("RPG", GenreOfOffer(s, "Коля"));
        }
    }

    [Theory]
    [InlineData("my-rpg")]
    [InlineData("my-rpg-roll")]
    public void A_later_finishers_own_change_of_his_roll_still_applies(string own)
    {
        foreach (var seed in s_seeds)
        {
            var s = KolyaPlaying(seed, shortMap: true);
            s.NextRandom(1, 1).Complete("Коля");
            Assert.Equal(2, s.Player("Коля").Finish!.Order);
            s.Give("Коля", own);
            if (own == "my-rpg")
            {
                s.Used("Коля", "my-rpg");
            }

            s.Roll("Коля");

            Assert.Equal("RPG", GenreOfOffer(s, "Коля"));
        }
    }

    [Fact]
    public void A_curse_from_before_the_finish_still_lowers_the_dice_after_it()
    {
        // One category, so a roll lands on the title; the d4s and the d6 are all 4, so the order they are drawn in does not matter
        var s = EconomyScenario.New().WithCategory("Horror")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 6, "Horror").WithGame("Alan Wake", 9, "Horror");
        s.WithMapLength(2).WithPlayers("Вася", "Петя", "Коля");
        WithFixtures(s);
        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(1, 1).Complete("Петя");
        s.RollTitle("Коля", "Dead Space").Start("Коля").WithPoints("Коля", 10);
        s.Give("Вася", "slow-curse");
        s.Used("Вася", "slow-curse", target: "Коля");
        s.NextRandom(1, 1).Complete("Коля");
        Assert.Equal(2, s.Player("Коля").Finish!.Order);
        Assert.Contains("slow-curse-effect", s.Inventory("Коля"));
        var points = s.Player("Коля").Points;

        s.RollTitle("Коля", "Alan Wake").Start("Коля").NextRandom(4, 4, 4, 4).Complete("Коля");

        Assert.Contains(s.LastEvents<EffectTriggered>(), e => e.ObjectId == "slow-curse-effect" && e.Trigger == Trigger.BeforeDice);
        Assert.Equal(points + 8, s.Player("Коля").Points);
        Assert.DoesNotContain("slow-curse-effect", s.Inventory("Коля"));
    }
}
