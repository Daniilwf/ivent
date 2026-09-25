using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Runs;

/// <summary>
/// The source of an hours estimate (W6; SPEC «Часы и кубы»: «игрок вводит оценку со ссылкой на источник»; D-44, D-96):
/// without hours in the snapshot the estimate needs a source (a link or a short note, up to
/// <see cref="Limits.MaxHoursSourceLength"/> characters), otherwise <c>run.hoursSourceRequired</c>. With hours in the
/// snapshot the estimate and the source are ignored.
/// </summary>
public class HoursSourceTests
{
    private const string Link = "https://howlongtobeat.com/game/2231";

    private static Scenario Playing(decimal? hours)
    {
        var s = Scenario.New()
            .WithCategory("Horror").WithGame("Silent Hill", hours, "Horror")
            .WithPlayers("Вася")
            .Roll("Вася").Start("Вася");
        ScenarioAssert.Accepted(s);
        return s;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Estimate_without_a_source_is_rejected(string? source)
    {
        var s = Playing(null);

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Complete("Вася", Difficulty.Normal, estimatedHours: 6, hoursSource: source), RejectionCodes.HoursSourceRequired);
        Assert.Equal(TurnPhase.Playing, s.Player("Вася").Phase);
    }

    [Fact]
    public void Estimate_with_a_source_is_stored_with_the_run()
    {
        var s = Playing(null);
        var runId = s.Player("Вася").ActiveRunId!.Value;
        var vasya = s.PlayerId("Вася");

        s.Complete("Вася", Difficulty.Normal, estimatedHours: 6, hoursSource: Link);

        ScenarioAssert.Accepted(s);
        Assert.Equal(
            new RunCompleted(runId, vasya, Difficulty.Normal, 6m, s.Clock.UtcNow, HoursSource: Link),
            Assert.Single(s.LastEvents<RunCompleted>()));
        Assert.Equal(Link, s.State.Runs[runId].HoursSource);
        Assert.Equal(6m, s.State.Runs[runId].Hours);
    }

    [Fact]
    public void Short_note_is_a_valid_source()
    {
        var s = Playing(null);

        s.Complete("Вася", Difficulty.Normal, estimatedHours: 4, hoursSource: "прошёл за вечер, по таймеру Steam");

        ScenarioAssert.Accepted(s);
        Assert.Equal("прошёл за вечер, по таймеру Steam", s.State.Runs.Values.Single().HoursSource);
    }

    [Fact]
    public void Source_of_the_maximum_length_is_accepted()
    {
        var s = Playing(null);
        var source = new string('я', Limits.MaxHoursSourceLength);

        s.Complete("Вася", Difficulty.Normal, estimatedHours: 4, hoursSource: source);

        ScenarioAssert.Accepted(s);
        Assert.Equal(source, s.State.Runs.Values.Single().HoursSource);
    }

    [Fact]
    public void Source_over_the_limit_is_rejected()
    {
        // D-96 (4): a too-long source has its own code
        var s = Playing(null);

        ScenarioAssert.RejectsWithoutChanges(
            s,
            x => x.Complete("Вася", Difficulty.Normal, estimatedHours: 4, hoursSource: new string('я', Limits.MaxHoursSourceLength + 1)),
            RejectionCodes.HoursSourceTooLong);
    }

    [Fact]
    public void Missing_source_is_checked_only_with_a_valid_estimate()
    {
        // No estimate at all is still «hours required», not «source required»
        var s = Playing(null);

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Complete("Вася", Difficulty.Normal), RejectionCodes.HoursRequired);
    }

    [Fact]
    public void With_pool_hours_the_estimate_and_the_source_are_ignored()
    {
        var s = Playing(6);
        var runId = s.Player("Вася").ActiveRunId!.Value;

        s.Complete("Вася", Difficulty.Normal, estimatedHours: 30, hoursSource: Link);

        ScenarioAssert.Accepted(s);
        var completed = Assert.Single(s.LastEvents<RunCompleted>());
        Assert.Equal(6m, completed.Hours);
        Assert.Null(completed.HoursSource);
        Assert.Null(s.State.Runs[runId].HoursSource);
    }

    [Fact]
    public void With_pool_hours_a_source_is_not_required()
    {
        var s = Playing(6);

        s.Complete("Вася", Difficulty.Normal);

        ScenarioAssert.Accepted(s);
        Assert.Null(Assert.Single(s.LastEvents<RunCompleted>()).HoursSource);
    }

    [Fact]
    public void With_pool_hours_an_over_long_source_is_ignored_as_well()
    {
        var s = Playing(6);

        s.Complete("Вася", Difficulty.Normal, hoursSource: new string('я', Limits.MaxHoursSourceLength + 1));

        ScenarioAssert.Accepted(s);
        Assert.Null(s.State.Runs.Values.Single().HoursSource);
    }
}
