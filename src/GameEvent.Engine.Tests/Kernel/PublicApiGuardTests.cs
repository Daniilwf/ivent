using GameEvent.Engine.Finish;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>
/// The engine's helpers the site calls directly refuse a missing argument at once, with its name, rather than failing
/// somewhere inside (CA1062). Found by mutation testing (D-282).
/// </summary>
public class PublicApiGuardTests
{
    [Fact]
    public void Read_side_helpers_refuse_missing_arguments()
    {
        var s = Scenario.New().WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithPlayers("Вася");
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        var run = s.State.Runs.Values.Single();

        Assert.Equal("rules", Assert.Throws<ArgumentNullException>(() => FinishBonusRules.Of(null!)).ParamName);
        Assert.Equal("state", Assert.Throws<ArgumentNullException>(() => FinishLine.First(null!)).ParamName);
        Assert.Equal("state", Assert.Throws<ArgumentNullException>(() => ProofReviewOrder.DecidesFinish(null!, run)).ParamName);
        Assert.Equal("run", Assert.Throws<ArgumentNullException>(() => ProofReviewOrder.DecidesFinish(s.State, null!)).ParamName);
        Assert.Equal("state", Assert.Throws<ArgumentNullException>(() => PoolStats.PlayersWithoutGames(null!, s.Context().Pool)).ParamName);
        Assert.Equal("pool", Assert.Throws<ArgumentNullException>(() => PoolStats.PlayersWithoutGames(s.State, null!)).ParamName);
    }
}
