using System.Globalization;
using System.Text;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Snapshots;

/// <summary>
/// Stage 2: the whole log of key graph map scenarios as stored (Verify, like <see cref="LogSnapshotTests"/>): a season
/// created with its map, a throw paused at a fork and continued by the branch, a teleport and a bonus on a stop, a drop
/// stopped by a checkpoint, a map published mid-season.
/// </summary>
public class MapLogSnapshotTests
{
    static MapLogSnapshotTests()
    {
        DiffEngine.DiffRunner.Disabled = true;
    }

    /// <summary>
    /// start → a → f → b1 → j → t → k → p → finish, f → c1 → j (Horror swamp); t a snake to a, p a bonus of 2, k a checkpoint.
    /// </summary>
    private static MapGraph Map() =>
        MapBuilder.New().Path("start", "a", "f", "b1", "j", "t", "k", "p", "q", "finish").Path("f", "c1", "j")
            .Teleport("t", to: "a").Checkpoint("k").Bonus("p", 2)
            .Zone(new ZoneDefinition { Id = "swamp", Name = "Болото", RollFilter = new GameFilterSpec { Tags = ["Horror"] }, DropPenaltyMultiplier = 1.5m }, "c1")
            .Build();

    private static Scenario Season() =>
        Scenario.New().WithMap(Map()).WithCategory("Any")
            .WithGame("Silent Hill", 9, "Any", "Horror").WithGame("Tetris", 3, "Any", "Puzzle").WithGame("Portal", 3, "Any", "Puzzle")
            .WithGame("Limbo", 3, "Any", "Puzzle").WithGame("Braid", 3, "Any", "Puzzle")
            .WithPlayers("Вася");

    [Fact]
    public Task Fork_teleport_bonus_checkpoint_and_zone()
    {
        var s = Season();

        // Three d4: 2 + 2 + 1 = 5 — a, f, then the branch: c1 (the swamp), j, t, and the snake to a
        s.NextRandom(0, 0).Roll("Вася").Start("Вася").NextRandom(2, 2, 1).Complete("Вася");
        s.ChooseBranch("Вася", "c1");
        ScenarioAssert.Accepted(s);

        // One d4 of 1 from a: the move ends on the fork f; the next throw asks for the branch first
        s.RollTitle("Вася", "Tetris").Start("Вася").NextRandom(1).Complete("Вася");
        s.RollTitle("Вася", "Portal").Start("Вася").NextRandom(4).Complete("Вася");
        s.ChooseBranch("Вася", "b1");
        ScenarioAssert.Accepted(s);

        // From k to the bonus p, then a drop stopped by the checkpoint k
        Assert.Equal("k", s.Player("Вася").CellId);
        s.RollTitle("Вася", "Limbo").Start("Вася").NextRandom(1).Complete("Вася");
        s.RollTitle("Вася", "Braid").Start("Вася").NextRandom(4, 4).Act(new DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);

        // A new version of the map mid-season
        s.Act(new PublishMap(Map() with { Cells = [.. Map().Cells.Select(c => c.Id == "q" ? c with { Type = CellType.PointsBonus, Amount = 5 } : c)] }, "Бонус на q"));
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
                    CreateSeason or PublishMap => command.GetType().Name,
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
