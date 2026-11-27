using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Statistics;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Statistics;

/// <summary>Attack counts and the MostAttacks nomination (SPEC «Итоги сезона» and «Взаимодействие игроков»).</summary>
public sealed class AttackStatisticsTests
{
    [Fact]
    public void There_are_no_candidates_without_a_landed_attack()
    {
        var uncreated = AttackStatistics.Build(SeasonState.Empty, []);
        Assert.Empty(uncreated.Players);
        Assert.Empty(uncreated.MostAttacksCandidates);

        var season = Season();
        var result = Read(season);
        AssertCounts(season, result, ("Вася", 0, 0), ("Петя", 0, 0), ("Коля", 0, 0), ("Маша", 0, 0));
        Assert.Empty(result.MostAttacksCandidates);
    }

    [Fact]
    public void One_item_counts_once_per_target_reached_and_interception_counts_for_neither_side()
    {
        var season = Season().WithCoins("Петя", 10).WithCoins("Коля", 10).WithCoins("Маша", 10);
        season.Give("Петя", "shield");
        season.Used("Петя", "shield");
        season.Give("Вася", "season-tax");

        season.Used("Вася", "season-tax");

        var intercepted = Assert.Single(season.LastEvents<HostileIntercepted>());
        Assert.Equal((season.PlayerId("Петя"), season.PlayerId("Вася")), (intercepted.PlayerId, intercepted.FromPlayerId));
        Assert.Equal(new[] { season.PlayerId("Коля"), season.PlayerId("Маша") }.Order(),
            season.LastEvents<HostileReceived>().Select(hit => hit.PlayerId).Order());
        Assert.All(season.LastEvents<HostileReceived>(), hit => Assert.Equal(season.PlayerId("Вася"), hit.FromPlayerId));
        var result = Read(season);
        AssertCounts(season, result, ("Вася", 2, 0), ("Петя", 0, 0), ("Коля", 0, 1), ("Маша", 0, 1));
        Assert.Equal(season.PlayerId("Вася"), Assert.Single(result.MostAttacksCandidates));
    }

    [Fact]
    public void Configured_nomination_returns_every_positive_tie_in_player_id_order_and_can_be_disabled()
    {
        var season = Season().WithCoins("Коля", 10).WithCoins("Маша", 10);
        Steal(season, "Петя", "Коля");
        Steal(season, "Вася", "Маша");

        var tied = Read(season);
        AssertCounts(season, tied, ("Вася", 1, 0), ("Петя", 1, 0), ("Коля", 0, 1), ("Маша", 0, 1));
        Assert.Equal(new[] { season.PlayerId("Петя"), season.PlayerId("Вася") }.Order(), tied.MostAttacksCandidates);

        Steal(season, "Вася", "Петя");
        var ahead = Read(season);
        AssertCounts(season, ahead, ("Вася", 2, 0), ("Петя", 1, 1), ("Коля", 0, 1), ("Маша", 0, 1));
        Assert.Equal(season.PlayerId("Вася"), Assert.Single(ahead.MostAttacksCandidates));

        season.WithRuleset(rules => rules with { Nominations = rules.Nominations with { Auto = [AutoNomination.MostDrops] } });
        ScenarioAssert.Accepted(season);
        var disabled = Read(season);
        Assert.Equal(ahead.Players, disabled.Players);
        Assert.Empty(disabled.MostAttacksCandidates);
    }

    [Fact]
    public void Undo_removes_only_its_attack_from_the_effective_log_and_replay_agrees()
    {
        var season = Season().WithCoins("Петя", 5).WithCoins("Маша", 5);
        Steal(season, "Коля", "Маша");
        Steal(season, "Вася", "Петя");
        var attack = season.LastCommandId;

        season.Act(new UndoCommand(attack, "Ошибся целью"));

        ScenarioAssert.Accepted(season);
        Assert.Equal(attack, Assert.Single(season.LastEvents<CommandUndone>()).CommandId);
        Assert.Equal(2, season.Log.OfType<HostileReceived>().Count());
        var remaining = Assert.Single(season.EffectiveLog.OfType<HostileReceived>());
        Assert.Equal((season.PlayerId("Маша"), season.PlayerId("Коля")), (remaining.PlayerId, remaining.FromPlayerId));
        var result = Read(season);
        AssertCounts(season, result, ("Вася", 0, 0), ("Петя", 0, 0), ("Коля", 1, 0), ("Маша", 0, 1));
        Assert.Equal(season.PlayerId("Коля"), Assert.Single(result.MostAttacksCandidates));
        Assert.Equal(result, AttackStatistics.Build(SeasonEngine.Replay(season.StoredLog), season.EffectiveLog));
    }

    private static Scenario Season()
    {
        var pack = EconomyScenario.ExamplePack("bird-thief", "shield", "shield-effect");
        var tax = new ObjectDefinition
        {
            Id = "season-tax",
            Kind = ObjectKind.Item,
            Name = "Сезонный сбор",
            Description = "Забирает по монетке у всех остальных игроков.",
            Rarity = Rarity.Common,
            Window = UseWindow.Anytime,
            Hostile = true,
            Stackable = true,
            Effect = new EffectSpec
            {
                Target = new TargetSpec { Selector = TargetSelector.All, ExcludeSelf = true },
                Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(-1) }],
            },
        };
        return EconomyScenario.New()
            .WithRuleset(rules => rules with { Nominations = rules.Nominations with { Auto = [AutoNomination.MostAttacks] } })
            .WithCategory("Horror").WithGame("Silent Hill", 6, "Horror")
            .WithPlayers("Вася", "Петя", "Коля", "Маша")
            .WithContent(pack with { Objects = [.. pack.Objects, tax] });
    }

    private static void Steal(Scenario season, string attacker, string recipient)
    {
        season.Give(attacker, "bird-thief");
        season.NextRandom(1).Used(attacker, "bird-thief", target: recipient);
        var hit = Assert.Single(season.LastEvents<HostileReceived>());
        Assert.Equal((season.PlayerId(recipient), season.PlayerId(attacker)), (hit.PlayerId, hit.FromPlayerId));
    }

    private static SeasonAttackStatistics Read(Scenario season) => AttackStatistics.Build(season.State, season.EffectiveLog);

    private static void AssertCounts(Scenario season, SeasonAttackStatistics result, params (string Player, int Made, int Received)[] expected) =>
        Assert.Equal(expected.Select(row => (season.PlayerId(row.Player), row.Made, row.Received)).OrderBy(row => row.Item1),
            result.Players.Select(row => (row.PlayerId, row.AttacksMade, row.AttacksReceived)));
}
