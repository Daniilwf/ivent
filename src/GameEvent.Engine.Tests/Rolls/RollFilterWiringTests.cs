using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rolls;

/// <summary>
/// G6, G12 wired into the roll (D-92): the wheel spins only over games the filters leave, and «is there anything to
/// roll» is one check, <c>Rolling.CanRoll</c>, with the same filters. A game counts only if it is available and lies in
/// a wheel category (weight &gt; 0). Stage 1 has no live filters, so test filters go through the internal
/// <c>Rolling.Draw</c> seam; seed-dependent outcomes run over many seeds.
/// </summary>
public class RollFilterWiringTests
{
    private static readonly int[] s_seeds = [.. Enumerable.Range(1, 60)];

    private static RollFilter Titles(RollFilterPriority priority, Scenario s, params string[] titles)
    {
        var ids = titles.Select(s.GameId).ToHashSet();
        return new RollFilter(priority, $"titles:{string.Join(",", titles)}", g => ids.Contains(g.Id));
    }

    private static RollFilter Tag(RollFilterPriority priority, string tag) =>
        new(priority, $"tag:{tag}", g => g.Tags.Contains(tag));

    private static RollFilter Nothing(RollFilterPriority priority) => new(priority, "nothing", _ => false);

    private static IGameEvent? Draw(Scenario s, string player, params RollFilter[] filters) =>
        Rolling.Draw(s.State, s.PlayerId(player), s.Context(), filters);

    private static bool CanRoll(Scenario s, string player, params RollFilter[] filters) =>
        Rolling.CanRoll(s.State, s.PlayerId(player), s.Context().Pool, filters);

    private static (string Category, IReadOnlyList<Guid> Games) Outcome(IGameEvent? drawn) =>
        drawn switch
        {
            GameRolled r => (r.Category, [r.GameId]),
            GameChoiceRolled c => (c.Category, [.. c.Offers.Select(o => o.GameId)]),
            null => throw new InvalidOperationException("Nothing was drawn."),
            _ => throw new InvalidOperationException($"Unexpected {drawn.GetType().Name}."),
        };

    /// <summary>Three categories, two games in each, and players Вася and Петя.</summary>
    private static Scenario Pool(int seed, int choiceCount = 1) =>
        Scenario.New(seed: seed)
            .WithRuleset(r => r with { Roll = r.Roll with { ChoiceCount = choiceCount } })
            .WithCategory("Horror", weight: 5).WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror")
            .WithCategory("Puzzle", weight: 1).WithGame("Tetris", 2, "Puzzle").WithGame("Portal", 5, "Puzzle")
            .WithCategory("RPG", weight: 5).WithGame("Witcher 3", 50, "RPG").WithGame("Gothic", 40, "RPG")
            .WithPlayers("Вася", "Петя");

    // ---- The filter narrows the wheel ----

    [Fact]
    public void Filter_narrows_the_wheel_to_the_categories_it_leaves()
    {
        var categories = new HashSet<string>();
        foreach (var seed in s_seeds)
        {
            // Given a filter that leaves Tetris and Silent Hill only
            var s = Pool(seed);
            var filter = Titles(RollFilterPriority.Effect, s, "Tetris", "Silent Hill");

            // When the wheel spins with it
            var (category, games) = Outcome(Draw(s, "Вася", filter));

            // Then RPG is never on the wheel and only the filtered games come out, each from its own category
            Assert.NotEqual("RPG", category);
            var game = Assert.Single(games);
            Assert.Equal(category == "Horror" ? s.GameId("Silent Hill") : s.GameId("Tetris"), game);
            categories.Add(category);
        }

        // Both categories the filter leaves stay on the wheel, the light Puzzle one included
        Assert.Equal(["Horror", "Puzzle"], categories.Order());
    }

    [Fact]
    public void Filter_narrows_a_choice_of_games_too()
    {
        foreach (var seed in s_seeds)
        {
            // Given a choice of 3 and a filter that leaves both horror games and Tetris
            var s = Pool(seed, choiceCount: 3);
            var filter = Titles(RollFilterPriority.Zone, s, "Silent Hill", "Alan Wake", "Tetris");

            var (category, games) = Outcome(Draw(s, "Вася", filter));

            // Then the options are only games the filter left
            var allowed = new[] { "Silent Hill", "Alan Wake", "Tetris" }.Select(s.GameId).ToHashSet();
            Assert.All(games, g => Assert.Contains(g, allowed));
            Assert.NotEqual("RPG", category);
        }
    }

    [Fact]
    public void Without_filters_draw_and_can_roll_see_the_whole_pool()
    {
        var categories = new HashSet<string>();
        foreach (var seed in s_seeds)
        {
            var s = Pool(seed);

            Assert.True(CanRoll(s, "Вася"));
            categories.Add(Outcome(Draw(s, "Вася")).Category);
        }

        Assert.Equal(["Horror", "Puzzle", "RPG"], categories.Order());
    }

    // ---- A special roll without games is refused, not turned into an ordinary one ----

    [Theory]
    [InlineData(RollFilterPriority.Effect)]
    [InlineData(RollFilterPriority.Normal)]
    public void Top_filter_matching_no_game_means_nothing_to_roll(RollFilterPriority priority)
    {
        foreach (var seed in s_seeds.Take(10))
        {
            var s = Pool(seed);

            Assert.Null(Draw(s, "Вася", Nothing(priority)));
            Assert.False(CanRoll(s, "Вася", Nothing(priority)));
        }
    }

    [Fact]
    public void Effect_matching_no_game_means_nothing_to_roll_even_with_a_zone_below()
    {
        var s = Pool(42);
        RollFilter[] filters = [Tag(RollFilterPriority.Zone, "Horror"), Nothing(RollFilterPriority.Effect)];

        Assert.Null(Draw(s, "Вася", filters));
        Assert.False(CanRoll(s, "Вася", filters));
    }

    [Fact]
    public void Effect_whose_games_are_all_busy_means_nothing_to_roll()
    {
        var sawBusy = false;
        foreach (var seed in s_seeds)
        {
            // Given Петя rolled one of two games; the effect «Puzzle» can only find Tetris
            var s = Scenario.New(seed: seed)
                .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror")
                .WithCategory("Puzzle").WithGame("Tetris", 2, "Puzzle")
                .WithPlayers("Вася", "Петя")
                .Roll("Петя");
            var puzzle = Tag(RollFilterPriority.Effect, "Puzzle");

            if (s.Player("Петя").Offer!.GameId == s.GameId("Tetris"))
            {
                // Then with Tetris busy the special roll has nothing: refused, not an ordinary roll of Silent Hill
                Assert.Null(Draw(s, "Вася", puzzle));
                Assert.False(CanRoll(s, "Вася", puzzle));
                sawBusy = true;
            }
            else
            {
                // Петя took Silent Hill: Tetris is free and the effect finds it
                Assert.True(CanRoll(s, "Вася", puzzle));
                Assert.Equal([s.GameId("Tetris")], Outcome(Draw(s, "Вася", puzzle)).Games);
            }
        }

        Assert.True(sawBusy, "Over many seeds Петя must sometimes take Tetris.");
    }

    [Fact]
    public void Zone_matching_no_game_is_dropped_and_the_roll_works()
    {
        foreach (var seed in s_seeds.Take(10))
        {
            var s = Pool(seed);

            Assert.True(CanRoll(s, "Вася", Nothing(RollFilterPriority.Zone)));
            Assert.NotNull(Draw(s, "Вася", Nothing(RollFilterPriority.Zone)));
        }
    }

    // ---- Games only in zero-weight categories do not count ----

    /// <summary>
    /// Horror and Puzzle are on the wheel; Secret is only in the zero-weight category Hidden, so the wheel never lands
    /// on it (weight 0 takes the category off the wheel).
    /// </summary>
    private static Scenario WithHidden(int seed) =>
        Scenario.New(seed: seed)
            .WithCategory("Horror").WithGame("Silent Hill", 12, "Horror").WithGame("Alan Wake", 15, "Horror")
            .WithCategory("Puzzle").WithGame("Tetris", 2, "Puzzle")
            .WithCategory("Hidden", weight: 0).WithGame("Secret", 3, "Hidden")
            .WithPlayers("Вася", "Петя");

    [Fact]
    public void Lower_filter_leaving_only_zero_weight_games_is_skipped()
    {
        foreach (var seed in s_seeds)
        {
            // Given the effect leaves Silent Hill and Secret, and the normal filter below it leaves only Secret
            var s = WithHidden(seed);
            RollFilter[] filters = [Titles(RollFilterPriority.Effect, s, "Silent Hill", "Secret"), Tag(RollFilterPriority.Normal, "Hidden")];

            // Then Secret cannot come off the wheel, the normal filter counts as empty and is skipped: Silent Hill
            Assert.True(CanRoll(s, "Вася", filters));
            var (category, games) = Outcome(Draw(s, "Вася", filters));
            Assert.Equal("Horror", category);
            Assert.Equal([s.GameId("Silent Hill")], games);
        }
    }

    [Fact]
    public void Top_zone_filter_leaving_only_zero_weight_games_is_dropped()
    {
        var categories = new HashSet<string>();
        foreach (var seed in s_seeds)
        {
            var s = WithHidden(seed);
            var zone = Tag(RollFilterPriority.Zone, "Hidden");

            // The zone leaves only Secret, which cannot be rolled: the zone is dropped and the whole wheel spins
            Assert.True(CanRoll(s, "Вася", zone));
            var (category, games) = Outcome(Draw(s, "Вася", zone));
            Assert.DoesNotContain(s.GameId("Secret"), games);
            categories.Add(category);
        }

        Assert.Equal(["Horror", "Puzzle"], categories.Order());
    }

    [Fact]
    public void Top_effect_leaving_only_zero_weight_games_means_nothing_to_roll()
    {
        var s = WithHidden(42);
        var effect = Tag(RollFilterPriority.Effect, "Hidden");

        Assert.False(CanRoll(s, "Вася", effect));
        Assert.Null(Draw(s, "Вася", effect));
    }

    // ---- CanRoll agrees with Draw ----

    [Fact]
    public void Can_roll_agrees_with_draw_over_random_pools_and_filters()
    {
        string[] tags = ["A", "B", "Z"];
        var sawEmpty = false;
        var sawDrawn = false;
        for (var seed = 1; seed <= 300; seed++)
        {
            // A deterministic generator local to the test (never the engine's random source)
            var gen = new Lcg(seed);
            var s = Scenario.New(seed: seed)
                .WithRuleset(r => r with { Roll = r.Roll with { ChoiceCount = 1 + gen.Next(3) } })
                .WithCategory("A", weight: 1 + gen.Next(3))
                .WithCategory("B", weight: gen.Next(3))
                .WithCategory("Z", weight: 0);
            var titles = new List<string>();
            var gameCount = gen.Next(6);
            for (var i = 0; i < gameCount; i++)
            {
                var title = $"Game {i}";
                var gameTags = tags.Where(_ => gen.Next(2) == 0).DefaultIfEmpty(tags[gen.Next(tags.Length)]).ToArray();
                if (gen.Next(6) == 0)
                {
                    s.WithDeletedGame(title, 1 + i, gameTags);
                }
                else
                {
                    s.WithGame(title, 1 + i, gameTags);
                }

                titles.Add(title);
            }

            s.WithPlayers("Вася", "Петя");

            // Петя may hold a game, and Вася may have excluded one (both make games unavailable to him)
            if (gen.Next(2) == 0)
            {
                s.Act(new RollGame(s.PlayerId("Петя")));
            }

            if (gen.Next(3) == 0)
            {
                s.Act(new RollGame(s.PlayerId("Вася")));
                if (s.Last.IsAccepted && s.Player("Вася").Offer is { } offer)
                {
                    s.Act(new DeclareAlreadyPlayed(s.PlayerId("Вася"), offer.GameId));
                    if (s.Player("Вася").Offer is not null || s.Player("Вася").Choice is not null)
                    {
                        s.Act(new AdjustPlayer(s.PlayerId("Вася"), "Сброс", DiscardOffer: true));
                    }
                }
            }

            if (s.Player("Вася").Phase != TurnPhase.Idle)
            {
                continue;
            }

            // Random filters: each a random subset of the pool with a random priority
            var filters = new List<RollFilter>();
            var filterCount = gen.Next(4);
            for (var f = 0; f < filterCount; f++)
            {
                var subset = titles.Where(_ => gen.Next(2) == 0).Select(s.GameId).ToHashSet();
                filters.Add(new RollFilter((RollFilterPriority)(1 + gen.Next(3)), $"random {f}", g => subset.Contains(g.Id)));
            }

            var canRoll = Rolling.CanRoll(s.State, s.PlayerId("Вася"), s.Context().Pool, filters);
            var drawn = Rolling.Draw(s.State, s.PlayerId("Вася"), s.Context(), filters);

            Assert.True(canRoll == drawn is not null, $"Seed {seed}: CanRoll {canRoll}, drawn {drawn}.");
            sawEmpty |= !canRoll;
            sawDrawn |= canRoll;
        }

        Assert.True(sawEmpty && sawDrawn, "Random pools must give both outcomes.");
    }

    /// <summary>Tiny deterministic generator so the random pools do not depend on the engine's random source.</summary>
    private sealed class Lcg(int seed)
    {
        private ulong _state = (ulong)seed * 6364136223846793005UL + 1442695040888963407UL;

        public int Next(int bound)
        {
            _state = (_state * 6364136223846793005UL) + 1442695040888963407UL;
            return (int)((_state >> 33) % (ulong)bound);
        }
    }
}
