using GameEvent.Engine.Duels;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Duels;

/// <summary>
/// EV42 for duels (D-510, D-525): the first finisher plays out of the race — he neither challenges nor is challenged, even
/// before his finish is approved, and his open duels end when he finishes, except the one his finishing completion wins
/// (without coins, D-414). Players who finished later duel for coins as before. On a map of 2 cells a completion with
/// dice 1, 1 reaches the finish.
/// </summary>
public class FirstFinisherDuelTests
{
    private static Scenario Season(Func<Engine.Rulesets.DuelRules, Engine.Rulesets.DuelRules>? duels = null) => DuelScenario.New(duels, mapLength: 2);

    [Theory]
    [InlineData("Вася", "Петя")]
    [InlineData("Петя", "Вася")]
    public void The_first_finisher_neither_challenges_nor_is_challenged_before_his_finish_is_approved(string challenger, string opponent)
    {
        var s = Season();
        s.CompleteTitle("Вася", "Silent Hill");
        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(s.State));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Challenge(challenger, opponent), RejectionCodes.DuelFirstFinisher);
    }

    [Fact]
    public void Finishing_first_ends_every_open_duel_of_the_finisher()
    {
        var s = Season(d => d with { MaxOpenPerPlayer = 2 });
        var incoming = s.Challenged("Коля", "Вася");
        var sent = s.Challenged("Вася", "Маша");
        s.Play("Вася", "Silent Hill");
        var race = s.StartDuel("Петя", "Вася"); // Silent Hill was played before the accept: it does not count

        s.CompleteActive("Вася");

        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(s.State));
        var now = s.Clock.UtcNow;
        Assert.Equal(
            new[] { new DuelExpired(incoming, DuelExpiry.FirstFinisher, now), new DuelExpired(sent, DuelExpiry.FirstFinisher, now) }.OrderBy(e => e.DuelId),
            s.LastEvents<DuelExpired>().OrderBy(e => e.DuelId));
        Assert.Equal(new DuelSettled(race, DuelStatus.Cancelled, DuelEnd.FirstFinisher, null, null, 0, now), Assert.Single(s.LastEvents<DuelSettled>()));
        Assert.Empty(s.DuelCoins());
        Assert.DoesNotContain(s.State.Duels.Values, d => d.IsOpen);
        Assert.Equal(s.State, Engine.Seasons.SeasonEngine.Replay(s.Log));
    }

    [Fact]
    public void The_completion_that_makes_him_first_wins_its_duel_without_coins()
    {
        var s = Season();
        var duel = s.StartDuel("Вася", "Петя");

        var run = s.CompleteTitle("Вася", "Silent Hill");

        Assert.Equal(s.PlayerId("Вася"), FinishLine.First(s.State));
        Assert.Equal(
            new DuelSettled(duel, DuelStatus.Won, DuelEnd.Completed, s.PlayerId("Вася"), run, 0, s.Clock.UtcNow),
            Assert.Single(s.LastEvents<DuelSettled>()));
        Assert.Empty(s.DuelCoins());
        Assert.Equal((DuelStatus.Won, 0), (s.Duel(duel).Status, s.Duel(duel).Coins));
    }

    [Fact]
    public void Duels_of_other_players_go_on_when_someone_finishes_first()
    {
        var s = Season();
        var duel = s.StartDuel("Петя", "Маша");

        s.CompleteTitle("Вася", "Silent Hill");

        Assert.Empty(s.DuelEvents());
        Assert.Equal(DuelStatus.Active, s.Duel(duel).Status);
    }

    [Fact]
    public void A_later_finisher_challenges_and_is_challenged_and_wins_coins()
    {
        var s = Season();
        s.CompleteTitle("Вася", "Silent Hill");
        s.CompleteTitle("Петя", "Doom 3");
        Assert.Equal(2, s.Player("Петя").Finish!.Order);

        var duel = s.StartDuel("Петя", "Маша");
        var run = s.CompleteTitle("Петя", "Alan Wake");

        var settled = Assert.Single(s.LastEvents<DuelSettled>());
        Assert.Equal((duel, DuelStatus.Won, (Guid?)s.PlayerId("Петя"), (Guid?)run, DuelScenario.RewardCoins), (settled.DuelId, settled.Status, settled.WinnerId, settled.RunId, settled.Coins));
        Assert.Equal(new CoinsChanged(s.PlayerId("Петя"), DuelScenario.RewardCoins, CoinsReason.Duel, run), Assert.Single(s.DuelCoins()));

        s.Challenge("Коля", "Петя");
        ScenarioAssert.Accepted(s);
    }
}
