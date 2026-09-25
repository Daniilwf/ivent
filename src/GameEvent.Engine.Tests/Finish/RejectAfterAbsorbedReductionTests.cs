using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Tests.Finish;

/// <summary>
/// Found by the long run (D-113 session): an earlier run cut after the finish is absorbed by the surplus (Q-3); when the
/// finishing run is then rejected, the token goes back by the finishing run's cells and by the absorbed cut together —
/// to where the earlier run, as corrected, really leads.
/// </summary>
public class RejectAfterAbsorbedReductionTests
{
    [Fact]
    public void A_reject_of_the_finishing_run_also_takes_back_the_cut_the_surplus_absorbed()
    {
        // Маша: Quake (dice 2, cells c1–c2), Silent Hill to the finish; Quake is cut to 1 after the finish (the surplus takes it)
        var s = Scenario.New(seed: 35);
        s.WithRuleset(r => r with { Roll = r.Roll with { ChoiceCount = 3, FreeRerollsPerRoll = 2 } });
        s.WithMapLength(6);
        s.WithCategory("Horror", weight: 3);
        s.WithGame("Silent Hill", 12m, "Horror");
        s.WithGame("Alan Wake", 15m, "Horror");
        s.WithCategory("Puzzle", weight: 2);
        s.WithGame("Tetris", 2m, "Puzzle");
        s.WithGame("Unknown Length", null, "Puzzle");
        s.WithCategory("Action", weight: 1);
        s.WithGame("Doom", 4m, "Action");
        s.WithGame("Dead Space", 9m, "Horror");
        s.WithGame("Portal", 3m, "Puzzle");
        s.WithGame("Limbo", null, "Puzzle");
        s.WithGame("Quake", 6m, "Action");
        s.WithGame("Prey", 18m, "Action");
        s.WithPlayers("Вася", "Петя", "Маша");
        s.Act(new RollGame(s.PlayerId("Маша")));
        s.Advance(TimeSpan.FromHours(12));
        s.Act(new MakeChoice(s.PlayerId("Маша"), Guid.Parse("00000000-0000-0000-0000-000000000001"), "20000000000000000000000000000009"));
        s.Advance(TimeSpan.FromHours(3));
        s.Act(new CompleteRun(s.PlayerId("Маша"), Difficulty.Easy, 1m, "HLTB"));
        s.Advance(TimeSpan.FromHours(21));
        s.Act(new AddSeasonPlayer(Guid.Parse("10000000-0000-0000-0000-000000000099"), Guid.Parse("40000000-0000-0000-0000-000000000099"), "Лёша", "c5", 1, 1));
        s.Advance(TimeSpan.FromHours(12));
        s.Act(new RollGame(s.PlayerId("Маша")));
        s.Advance(TimeSpan.FromHours(12));
        s.Act(new MakeChoice(s.PlayerId("Маша"), Guid.Parse("00000000-0000-0000-0000-000000000003"), "20000000000000000000000000000001"));
        s.Advance(TimeSpan.FromHours(15));
        s.Act(new CompleteRun(s.PlayerId("Маша"), Difficulty.Normal, 2m, "HLTB"));
        s.Advance(TimeSpan.FromHours(21));
        s.Act(new ChangeRunDifficulty(Guid.Parse("00000000-0000-0000-0000-000000000004") /* run: Маша, Silent Hill */, Difficulty.Extreme, "сложность по пруфу"));
        s.Act(new RollGame(s.PlayerId("Вася")));
        s.Advance(TimeSpan.FromHours(21));
        s.Act(new ChangeSeasonStatus(SeasonStatus.Closing));
        s.Advance(TimeSpan.FromHours(9));
        s.Act(new CorrectRunHours(Guid.Parse("00000000-0000-0000-0000-000000000002") /* run: Маша, Quake */, 3m, "часы по пруфу"));
        s.Advance(TimeSpan.FromHours(6));
        s.Act(new RejectProof(Guid.Parse("00000000-0000-0000-0000-000000000004") /* run: Маша, Silent Hill */, "на скрине другая игра"));

        // The finish is revoked; back 5 cells — Silent Hill's 4 and the cut of Quake — to c1, Quake's corrected sum
        ScenarioAssert.Accepted(s);
        var back = Assert.Single(s.LastEvents<PlayerMoved>());
        Assert.Equal((-5, "c1"), (back.Steps, back.To));
        Assert.Null(s.Player("Маша").Finish);
        Assert.Equal("c1", s.Player("Маша").CellId);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }
}
