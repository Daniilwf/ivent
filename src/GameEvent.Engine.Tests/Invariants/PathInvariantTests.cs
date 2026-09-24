using FsCheck.Xunit;
using GameEvent.Engine.Map;

namespace GameEvent.Engine.Tests.Invariants;

/// <summary>
/// Random walks on a map with a merge (M4, D-90): forward along default edges, transfers to any cell but the
/// finish, and moves back. The path folded by <see cref="PlayerPath.After"/> must agree with
/// <see cref="Movement.Backward"/> and <see cref="Movement.Forward"/>, so replaying the log gives the same path.
/// Each byte of <c>script</c> is one move.
/// </summary>
public class PathInvariantTests
{
    // start → a1 → m → x → finish, start → b1 → m, with a1 → m the primary edge into m
    private static readonly MapGraph s_map = new(
        [
            new Cell("start", CellType.Start),
            new Cell("a1", CellType.Empty),
            new Cell("b1", CellType.Empty),
            new Cell("m", CellType.Empty),
            new Cell("x", CellType.Empty),
            new Cell("finish", CellType.Finish),
        ],
        [
            new Edge("start", "a1", IsDefaultForward: true, IsPrimaryBackward: true),
            new Edge("start", "b1", IsDefaultForward: false, IsPrimaryBackward: true),
            new Edge("a1", "m", IsDefaultForward: true, IsPrimaryBackward: true),
            new Edge("b1", "m", IsDefaultForward: true, IsPrimaryBackward: false),
            new Edge("m", "x", IsDefaultForward: true, IsPrimaryBackward: true),
            new Edge("x", "finish", IsDefaultForward: true, IsPrimaryBackward: true),
        ]);

    private static readonly string[] s_transferTargets = ["start", "a1", "b1", "m", "x"];

    [Property(MaxTest = 300)]
    public void Folded_path_agrees_with_movement(byte[] script)
    {
        var path = PlayerPath.At("start");
        foreach (var b in script ?? [])
        {
            var before = path;
            var n = (b >> 2) % 6;
            var moved = (b & 3) switch
            {
                0 => Transfer(path, s_transferTargets[n % s_transferTargets.Length]),
                1 => Step(path, n, Movement.Forward(s_map, path.Current, n)),
                _ => Step(path, -n, Movement.Backward(s_map, path, n)),
            };
            if (moved is null)
            {
                continue;
            }

            path = path.After(moved);

            Assert.Equal(moved.To, path.Current);
            Assert.Equal(before.Segments.Count + (moved.Steps == 0 ? 1 : 0), path.Segments.Count);
            foreach (var segment in path.Segments)
            {
                Assert.All(segment.Cells.Zip(segment.Cells.Skip(1)), pair =>
                    Assert.Contains(s_map.Edges, e => e.From == pair.First && e.To == pair.Second));
            }

            if (moved.Steps > 0)
            {
                // Going straight back retraces the cells just entered
                Assert.Equal([.. moved.Path.Reverse().Skip(1), moved.From], Movement.Backward(s_map, path, moved.Path.Count));
            }
        }
    }

    private static PlayerMoved Transfer(PlayerPath path, string to) =>
        new(Guid.Empty, path.Current, to, 0, [to], MoveReason.AdminAdjustment, RunId: null);

    // A blocked move writes no event (D-47, D-90)
    private static PlayerMoved? Step(PlayerPath path, int steps, IReadOnlyList<string> entered) =>
        entered.Count == 0
            ? null
            : new PlayerMoved(Guid.Empty, path.Current, entered[^1], steps, [.. entered], MoveReason.CompletionRoll, RunId: null);
}
