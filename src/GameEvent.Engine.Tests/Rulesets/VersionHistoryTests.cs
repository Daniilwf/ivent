using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Rulesets;

/// <summary>
/// C3 / D-82: the rules live in the season log. CreateSeason brings version 1, every ChangeRuleset is a new
/// version stored whole in RulesetChanged, the state holds the rules in force and their version.
/// An invalid ruleset is never stored (C2). The «было/стало» history is a diff of consecutive versions.
/// </summary>
public class VersionHistoryTests
{
    private static readonly Guid s_seasonId = SequentialIds.Make(0x30000000, 1);

    private static Ruleset HoursPerDie(Ruleset r, decimal hours) =>
        r with { Reward = r.Reward with { DiceCount = r.Reward.DiceCount with { HoursPerDie = hours } } };

    private static Ruleset Invalid(Ruleset r) => r with { Map = r.Map with { LinearLength = 0 } };

    private static Scenario Season() => Scenario.New().WithPlayers("Вася");

    // ---- CreateSeason ----

    [Fact]
    public void Season_is_created_with_ruleset_version_1_and_its_rules_in_the_state()
    {
        var s = Scenario.New();
        var rules = s.Ruleset;

        s.Act(new CreateSeason(s_seasonId, "Тестовый сезон", rules));

        ScenarioAssert.Accepted(s);
        var created = Assert.Single(s.LastEvents<SeasonCreated>());
        Assert.Equal(rules, created.Ruleset);
        Assert.Equal(1, s.State.RulesetVersion);
        Assert.Equal(rules, s.State.Rules);
    }

    [Fact]
    public void Season_with_an_invalid_ruleset_is_not_created()
    {
        var s = Scenario.New();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new CreateSeason(s_seasonId, "Тестовый сезон", Invalid(x.Ruleset))), RejectionCodes.RulesetInvalid);
        Assert.False(s.State.IsCreated);
        Assert.Equal(0, s.State.RulesetVersion);
    }

    [Fact]
    public void Rejection_of_an_invalid_ruleset_names_the_field()
    {
        var s = Scenario.New();

        s.Act(new CreateSeason(s_seasonId, "Тестовый сезон", Invalid(s.Ruleset)));

        Assert.Equal(RejectionCodes.RulesetInvalid, s.Last.Rejection!.Code);
        Assert.Contains("map.linearLength", s.Last.Rejection.Detail, StringComparison.Ordinal);
    }

    // ---- ChangeRuleset ----

    [Fact]
    public void Change_is_a_new_version_with_the_whole_ruleset()
    {
        var s = Season();
        var changed = HoursPerDie(s.Ruleset, 2);

        s.Act(new ChangeRuleset(changed));

        ScenarioAssert.Accepted(s);
        var e = Assert.Single(s.Last.Events);
        Assert.Equal(new RulesetChanged(2, changed), e);
        Assert.Equal(2, s.State.RulesetVersion);
        Assert.Equal(changed, s.State.Rules);
    }

    [Fact]
    public void Each_change_bumps_the_version_by_one()
    {
        var s = Season();

        s.Act(new ChangeRuleset(HoursPerDie(s.Ruleset, 2)));
        s.Act(new ChangeRuleset(HoursPerDie(s.Ruleset, 4)));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new RulesetChanged(3, HoursPerDie(TestRuleset.Create(), 4)), Assert.Single(s.Last.Events));
        Assert.Equal(3, s.State.RulesetVersion);
        Assert.Equal(4, s.State.Rules.Reward.DiceCount.HoursPerDie);
    }

    [Fact]
    public void Going_back_to_an_earlier_ruleset_is_still_a_new_version()
    {
        var s = Season();
        var original = s.Ruleset;
        s.Act(new ChangeRuleset(HoursPerDie(original, 2)));

        s.Act(new ChangeRuleset(original));

        ScenarioAssert.Accepted(s);
        Assert.Equal(3, s.State.RulesetVersion);
        Assert.Equal(original, s.State.Rules);
    }

    [Fact]
    public void Same_ruleset_is_rejected_as_unchanged()
    {
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeRuleset(x.Ruleset)), RejectionCodes.RulesetUnchanged);
        Assert.Equal(1, s.State.RulesetVersion);
    }

    [Fact]
    public void Equal_ruleset_read_again_from_json_is_rejected_as_unchanged()
    {
        // Compared by content, not by reference: the admin saves the same JSON again
        var s = Season();

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new ChangeRuleset(TestRuleset.Create())), RejectionCodes.RulesetUnchanged);
    }

    [Fact]
    public void Invalid_change_is_rejected_and_the_version_stays()
    {
        var s = Season();
        s.Act(new ChangeRuleset(HoursPerDie(s.Ruleset, 2)));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeRuleset(Invalid(x.Ruleset))), RejectionCodes.RulesetInvalid);
        Assert.Equal(2, s.State.RulesetVersion);
        Assert.Equal(2, s.State.Rules.Reward.DiceCount.HoursPerDie);
    }

    [Fact]
    public void Change_before_the_season_exists_is_rejected()
    {
        var s = Scenario.New();

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new ChangeRuleset(TestRuleset.Create())), RejectionCodes.SeasonNotCreated);
    }

    [Fact]
    public void Changing_the_map_length_does_not_rebuild_the_map()
    {
        // The map is fixed at creation; map.linearLength only matters for a new season
        var s = Season();
        var map = s.State.Map;

        s.Act(new ChangeRuleset(s.Ruleset with { Map = s.Ruleset.Map with { LinearLength = s.Ruleset.Map.LinearLength + 10 } }));

        ScenarioAssert.Accepted(s);
        Assert.Equal(map, s.State.Map);
    }

    [Fact]
    public void Version_history_is_the_ruleset_events_of_the_log()
    {
        var s = Season();
        s.Act(new ChangeRuleset(HoursPerDie(s.Ruleset, 2)));
        s.Act(new ChangeRuleset(HoursPerDie(s.Ruleset, 4)));

        var versions = s.Log.OfType<RulesetChanged>().Select(e => e.Version).ToList();

        Assert.Equal([2, 3], versions);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
    }
}
