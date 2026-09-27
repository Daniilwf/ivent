using System.Globalization;
using System.Text;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Snapshots;

/// <summary>
/// Stage 4: the whole log of key economy scenarios as stored (Verify, like <see cref="LogSnapshotTests"/>, D-112): items on
/// other players with an interception and a curse on the throw; the shop with a coupon, a purchase, the growing price, the
/// lots vanishing and the price starting over; a bet won and taken back by a reject.
/// </summary>
public class EconomyLogSnapshotTests
{
    static EconomyLogSnapshotTests()
    {
        DiffEngine.DiffRunner.Disabled = true;
    }

    private static Scenario Season(params string[] objects)
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror").WithPlayers("Вася", "Петя");
        var pack = EconomyScenario.RepositoryPack();
        return s.WithContent(pack with { Objects = [.. pack.Objects.Where(o => objects.Contains(o.Id))], Wheels = [] });
    }

    [Fact]
    public Task Items_on_another_player_an_interception_and_a_curse_on_the_throw()
    {
        var s = Season("orange", "lucky-die", "shield", "shield-effect", "bird-thief", "curse", "curse-effect");
        s.WithCoins("Петя", 5);

        // Петя puts up a shield; Вася's thief is stopped by it, the second one gets through
        s.Give("Петя", "shield");
        s.Used("Петя", "shield");
        s.Give("Вася", "bird-thief");
        s.Used("Вася", "bird-thief", target: "Петя");
        s.Give("Вася", "bird-thief");
        s.NextRandom(3).Used("Вася", "bird-thief", target: "Петя");

        // Вася curses Петя, whose next throw is 1d6 lower, not below 1; Вася's own throw gets the lucky die
        s.Give("Вася", "curse");
        s.Used("Вася", "curse", target: "Петя");
        s.RollTitle("Петя", "Silent Hill").Start("Петя").NextRandom(2, 1, 5).Complete("Петя");
        s.RollTitle("Вася", "Dead Space").Start("Вася");
        s.Give("Вася", "lucky-die");
        s.Used("Вася", "lucky-die");
        s.NextRandom(1, 2, 3, 6).Complete("Вася");
        ScenarioAssert.Accepted(s);

        return Verifier.Verify(Render(s));
    }

    [Fact]
    public Task Shop_with_a_coupon_a_purchase_the_price_and_the_lots_vanishing()
    {
        var s = Season("orange", "shop-coupon", "shield", "shield-effect", "curse", "curse-effect");
        s.WithCoins("Вася", 40);

        s.Give("Вася", "shop-coupon");
        s.Used("Вася", "shop-coupon");
        s.NextRandom(0, 0, 0, 0, 0, 0).RollShop("Вася");
        s.Buy("Вася", 0);
        s.NextRandom(0, 0, 0, 0, 0, 0).RollShop("Вася");
        s.NextRandom(0, 0, 0, 0, 0, 0).RollShop("Вася");
        s.Advance(TimeSpan.FromMinutes(15));
        s.FireTimers();
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1).Complete("Вася");
        ScenarioAssert.Accepted(s);

        return Verifier.Verify(Render(s));
    }

    [Fact]
    public Task Bet_won_and_taken_back_by_a_reject()
    {
        var s = Season("orange");
        s.WithCoins("Вася", 10);
        s.RollTitle("Петя", "Silent Hill").Start("Петя");

        s.Bet("Вася", "Петя", days: 3, stake: 5);
        s.Advance(TimeSpan.FromDays(1));
        s.NextRandom(1, 1).Complete("Петя");
        var run = s.State.Runs.Values.Single(r => r.PlayerId == s.PlayerId("Петя"));
        s.Act(new RejectProof(run.RunId, "Нет титров"));
        ScenarioAssert.Accepted(s);

        return Verifier.Verify(Render(s));
    }

    private static string Render(Scenario s)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var text = new StringBuilder();
            for (var i = 0; i < s.History.Count; i++)
            {
                var command = s.Commands[i];
                text.Append("# ").Append(command switch
                {
                    null => "(crafted)",
                    CreateSeason or PublishContent => command.GetType().Name,
                    _ => command.ToString(),
                }).Append('\n');
                foreach (var e in s.History[i].Events)
                {
                    var stored = EventCodec.Encode(e);
                    Assert.Equal(e, EventCodec.Decode(stored));
                    text.Append(stored.Type).Append(" v").Append(stored.Version).Append(' ').Append(stored.Data).Append('\n');
                }
            }

            return text.ToString();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
