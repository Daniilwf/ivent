using GameEvent.Engine.Challenges;
using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Challenges;

/// <summary>
/// EV33: the weekly challenge (SPEC «Челлендж недели», D-526, task 5.13). The admin sets a challenge of the content format
/// (CONTENT.md «Олдскул») from a start; its window is <c>weeklyChallenge.durationDays</c>. An automatic one is checked by a
/// calculation after the completion (D-413, not an effect): a run completed in the window whose game meets the condition
/// gives the player the reward once. A manual one is marked by the admin with the run. A reject takes the completion and
/// the coins back; the frozen first finisher completes without coins (as a bet, D-414). Written from the spec before the
/// code: the contract (command, event and state names) is the one proposed in D-570.
/// </summary>
public class WeeklyChallengeTests
{
    private static int ChallengeCoins(Scenario s, string player) =>
        s.Last.Events.OfType<CoinsChanged>()
            .Where(c => c.PlayerId == s.PlayerId(player) && c.Reason is CoinsReason.WeeklyChallenge or CoinsReason.WeeklyChallengeRevoked)
            .Sum(c => c.Delta);

    // ---- S5.13-01: «Олдскул» from CONTENT.md, the acceptance test ----

    [Fact]
    public void Setting_old_school_opens_a_window_of_the_configured_days_with_the_reward_of_the_definition()
    {
        var s = WeeklyChallengeScenario.New();
        var start = s.Clock.UtcNow;

        s.Act(new SetWeeklyChallenge(WeeklyChallengeScenario.OldSchool(), start));

        ScenarioAssert.Accepted(s);
        var set = Assert.Single(s.Last.Events.OfType<WeeklyChallengeSet>());
        Assert.Equal(("old-school", start, start.AddDays(WeeklyChallengeScenario.WindowDays), 10), (set.Definition.Id, set.StartsAt, set.EndsAt, set.RewardCoins));
        var challenge = s.State.WeeklyChallenges[set.ChallengeId];
        Assert.Equal((set.StartsAt, set.EndsAt, 10), (challenge.StartsAt, challenge.EndsAt, challenge.RewardCoins));
        Assert.Empty(challenge.Completions);
    }

    [Fact]
    public void Old_school_pays_ten_coins_when_a_game_released_before_2005_is_completed_in_the_window()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var coins = s.Player("Вася").Coins;

        var run = s.CompleteTitle("Вася", "Silent Hill 3");

        Assert.Equal(new WeeklyChallengeCompleted(challenge, s.PlayerId("Вася"), run, 10), Assert.Single(s.LastEvents<WeeklyChallengeCompleted>()));
        Assert.Contains(new CoinsChanged(s.PlayerId("Вася"), 10, CoinsReason.WeeklyChallenge, run), s.Last.Events);
        Assert.Equal(coins + 6 + 10, s.Player("Вася").Coins);
        Assert.Equal([s.PlayerId("Вася")], s.Completers(challenge));
    }

    [Fact]
    public void The_challenge_is_settled_after_the_completion_itself()
    {
        // A calculation after the command (D-413): the run is completed and paid before the challenge looks at it
        var s = WeeklyChallengeScenario.New();
        s.SetChallenge(WeeklyChallengeScenario.OldSchool());

        s.CompleteTitle("Вася", "Silent Hill 3");

        var events = s.Last.Events.ToList();
        var completed = events.FindIndex(e => e is RunCompleted);
        var reward = events.FindIndex(e => e is CoinsChanged { Reason: CoinsReason.CompletionReward });
        var challenge = events.FindIndex(e => e is WeeklyChallengeCompleted);
        Assert.True(completed >= 0 && reward > completed && challenge > reward, string.Join(", ", s.LastTypes()));
    }

    // ---- S5.13-02: the border of the filter ----

    [Theory]
    [InlineData("Doom 3", true)] // 2004
    [InlineData("Resident Evil 2", true)] // 1998
    [InlineData("F.E.A.R.", false)] // 2005: «до 2005 года» does not take 2005 itself
    [InlineData("Alan Wake", false)] // 2010
    [InlineData("Indie Horror", false)] // no year: a year filter does not pass it (D-307)
    public void Old_school_takes_only_games_released_before_2005(string title, bool completes)
    {
        var s = WeeklyChallengeScenario.New();
        s.SetChallenge(WeeklyChallengeScenario.OldSchool());

        s.CompleteTitle("Вася", title);

        Assert.Equal(completes, s.LastEvents<WeeklyChallengeCompleted>().Any());
        Assert.Equal(completes ? 10 : 0, ChallengeCoins(s, "Вася"));
    }

    [Theory]
    [InlineData(Difficulty.Normal, false)]
    [InlineData(Difficulty.Hard, true)]
    public void A_condition_on_the_difficulty_reads_the_completed_run(Difficulty difficulty, bool completes)
    {
        var s = WeeklyChallengeScenario.New();
        s.SetChallenge(WeeklyChallengeScenario.HardWeek());

        s.CompleteTitle("Вася", "Alan Wake", difficulty);

        Assert.Equal(completes, s.LastEvents<WeeklyChallengeCompleted>().Any());
    }

    // ---- S5.13-03: the window ----

    [Theory]
    [InlineData(-1, false)] // a minute before the start
    [InlineData(0, true)] // the start itself
    [InlineData((WeeklyChallengeScenario.WindowDays * 24 * 60) - 1, true)] // the last minute
    [InlineData((WeeklyChallengeScenario.WindowDays * 24 * 60) + 1, false)] // 7 days and a minute
    public void Only_a_completion_inside_the_window_counts(int minutesFromStart, bool completes)
    {
        // The run is rolled before the window opens: what counts is when it is completed (D-526)
        var s = WeeklyChallengeScenario.New();
        var start = s.Clock.UtcNow.AddDays(1);
        s.SetChallenge(WeeklyChallengeScenario.OldSchool(), start);
        s.Play("Вася", "Silent Hill 3");

        s.Advance(start.AddMinutes(minutesFromStart) - s.Clock.UtcNow);
        s.NextRandom(1, 1).Complete("Вася");

        Assert.Equal(completes, s.LastEvents<WeeklyChallengeCompleted>().Any());
    }

    [Fact]
    public void A_challenge_may_start_later_than_it_is_set()
    {
        var s = WeeklyChallengeScenario.New();
        var start = s.Clock.UtcNow.AddDays(2);

        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool(), start);

        Assert.Equal(start, s.State.WeeklyChallenges[challenge].StartsAt);
        s.CompleteTitle("Вася", "Silent Hill 3");
        Assert.Empty(s.LastEvents<WeeklyChallengeCompleted>());
    }

    // ---- S5.13-04: one reward per player ----

    [Fact]
    public void A_player_gets_the_reward_of_a_challenge_once()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        s.CompleteTitle("Вася", "Silent Hill 3");
        var coins = s.Player("Вася").Coins;

        s.CompleteTitle("Вася", "Doom 3");

        Assert.Empty(s.LastEvents<WeeklyChallengeCompleted>());
        Assert.Equal(coins + 6, s.Player("Вася").Coins);
        Assert.Equal([s.PlayerId("Вася")], s.Completers(challenge));
    }

    [Fact]
    public void Every_player_gets_their_own_reward()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());

        s.CompleteTitle("Вася", "Silent Hill 3");
        s.CompleteTitle("Петя", "Doom 3");

        Assert.Equal(10, ChallengeCoins(s, "Петя"));
        Assert.Equal(new[] { s.PlayerId("Вася"), s.PlayerId("Петя") }.Order(), s.Completers(challenge).Order());
    }

    // ---- S5.13-10: several challenges at once ----

    [Fact]
    public void One_run_completes_every_challenge_it_meets()
    {
        var s = WeeklyChallengeScenario.New();
        var oldSchool = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var horror = s.SetChallenge(WeeklyChallengeScenario.HorrorWeek(coins: 5));
        var hard = s.SetChallenge(WeeklyChallengeScenario.HardWeek());

        var run = s.CompleteTitle("Вася", "Silent Hill 3");

        Assert.Equal(
            new[] { (oldSchool, 10), (horror, 5) }.OrderBy(c => c.Item1),
            s.LastEvents<WeeklyChallengeCompleted>().Select(c => (c.ChallengeId, c.Coins)).OrderBy(c => c.ChallengeId));
        Assert.All(s.LastEvents<WeeklyChallengeCompleted>(), c => Assert.Equal(run, c.RunId));
        Assert.Equal(15, ChallengeCoins(s, "Вася"));
        Assert.Empty(s.Completers(hard));
    }

    // ---- S5.13-06: a reject takes the completion back ----

    [Fact]
    public void A_reject_takes_the_completion_and_the_coins_back()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var run = s.CompleteTitle("Вася", "Silent Hill 3");
        var coins = s.Player("Вася").Coins;

        s.Act(new RejectProof(run, "На скрине другая игра"));

        ScenarioAssert.Accepted(s);
        Assert.Equal(new WeeklyChallengeRevoked(challenge, s.PlayerId("Вася"), run, 10), Assert.Single(s.LastEvents<WeeklyChallengeRevoked>()));
        Assert.Contains(new CoinsChanged(s.PlayerId("Вася"), -10, CoinsReason.WeeklyChallengeRevoked, run), s.Last.Events);
        Assert.Equal(coins - 6 - 10, s.Player("Вася").Coins);
        Assert.Empty(s.Completers(challenge));
    }

    [Fact]
    public void A_reject_with_the_drop_penalty_takes_it_back_too()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var run = s.CompleteTitle("Вася", "Silent Hill 3");

        s.NextRandom(1, 1).Act(new RejectProofWithDropPenalty(run, "Фейк"));

        ScenarioAssert.Accepted(s);
        Assert.Single(s.LastEvents<WeeklyChallengeRevoked>());
        Assert.Equal(-10, ChallengeCoins(s, "Вася"));
        Assert.Empty(s.Completers(challenge));
    }

    [Fact]
    public void After_a_reject_a_new_fitting_completion_pays_again()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var first = s.CompleteTitle("Вася", "Silent Hill 3");
        s.Act(new RejectProof(first, "Нет титров"));

        var second = s.CompleteTitle("Вася", "Doom 3");

        Assert.Equal(new WeeklyChallengeCompleted(challenge, s.PlayerId("Вася"), second, 10), Assert.Single(s.LastEvents<WeeklyChallengeCompleted>()));
        Assert.Equal([s.PlayerId("Вася")], s.Completers(challenge));
    }

    [Fact]
    public void Rejecting_a_run_that_did_not_complete_the_challenge_takes_nothing_back()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        s.CompleteTitle("Вася", "Silent Hill 3");
        var other = s.CompleteTitle("Вася", "Doom 3"); // fits, but the reward is already taken

        s.Act(new RejectProof(other, "Нет титров"));

        Assert.Empty(s.LastEvents<WeeklyChallengeRevoked>());
        Assert.Equal([s.PlayerId("Вася")], s.Completers(challenge));
    }

    [Fact]
    public void Rejecting_another_players_run_leaves_the_completion()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        s.CompleteTitle("Вася", "Silent Hill 3");
        var petya = s.CompleteTitle("Петя", "Doom 3");

        s.Act(new RejectProof(petya, "Нет титров"));

        Assert.Equal(new WeeklyChallengeRevoked(challenge, s.PlayerId("Петя"), petya, 10), Assert.Single(s.LastEvents<WeeklyChallengeRevoked>()));
        Assert.Equal([s.PlayerId("Вася")], s.Completers(challenge));
    }

    [Fact]
    public void Approving_the_proof_keeps_the_completion()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var run = s.CompleteTitle("Вася", "Silent Hill 3");

        s.Act(new ApproveProof(run, null, "Видел на стриме"));

        ScenarioAssert.Accepted(s);
        Assert.Empty(s.LastEvents<WeeklyChallengeRevoked>());
        Assert.Empty(s.LastEvents<WeeklyChallengeCompleted>());
        Assert.Equal([s.PlayerId("Вася")], s.Completers(challenge));
    }

    // ---- S5.13-07: the frozen first finisher ----

    [Fact]
    public void The_frozen_first_finisher_completes_the_challenge_without_coins()
    {
        var s = WeeklyChallengeScenario.New(r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } }, mapLength: 2);
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        s.CompleteTitle("Вася", "Alan Wake"); // 2 steps: the finish
        Assert.True(s.Player("Вася").Finish!.Frozen);

        var run = s.CompleteTitle("Вася", "Silent Hill 3");

        Assert.Equal(new WeeklyChallengeCompleted(challenge, s.PlayerId("Вася"), run, 0), Assert.Single(s.LastEvents<WeeklyChallengeCompleted>()));
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), c => c.Reason == CoinsReason.WeeklyChallenge);
        Assert.Equal([s.PlayerId("Вася")], s.Completers(challenge));
    }

    [Fact]
    public void A_later_finisher_completes_the_challenge_with_coins()
    {
        // Finishers after the first are not frozen: points and coins keep coming (D-424)
        var s = WeeklyChallengeScenario.New(r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } }, mapLength: 2);
        s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        s.CompleteTitle("Вася", "Alan Wake");
        s.CompleteTitle("Петя", "Outlast");
        Assert.False(s.Player("Петя").Finish!.Frozen);

        s.CompleteTitle("Петя", "Silent Hill 3");

        Assert.Equal(10, Assert.Single(s.LastEvents<WeeklyChallengeCompleted>()).Coins);
        Assert.Equal(10, ChallengeCoins(s, "Петя"));
    }

    // ---- S5.13-05: the manual challenge ----

    [Fact]
    public void A_manual_challenge_is_not_checked_by_the_completion()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.Manual());

        s.CompleteTitle("Вася", "Alan Wake");

        Assert.Empty(s.LastEvents<WeeklyChallengeCompleted>());
        Assert.Empty(s.Completers(challenge));
    }

    [Fact]
    public void The_admin_marks_a_player_of_a_manual_challenge_with_the_run_and_the_reward_of_the_definition_is_paid()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.Manual(coins: 15));
        var run = s.CompleteTitle("Вася", "Alan Wake");
        var coins = s.Player("Вася").Coins;

        s.Mark(challenge, "Вася", run);

        ScenarioAssert.Accepted(s);
        Assert.Equal(new WeeklyChallengeCompleted(challenge, s.PlayerId("Вася"), run, 15), Assert.Single(s.LastEvents<WeeklyChallengeCompleted>()));
        Assert.Contains(new CoinsChanged(s.PlayerId("Вася"), 15, CoinsReason.WeeklyChallenge, run), s.Last.Events);
        Assert.Equal(coins + 15, s.Player("Вася").Coins);
    }

    [Fact]
    public void A_manual_completion_is_taken_back_by_a_reject()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.Manual(coins: 15));
        var run = s.CompleteTitle("Вася", "Alan Wake");
        s.Mark(challenge, "Вася", run);

        s.Act(new RejectProof(run, "Записи стрима нет"));

        Assert.Equal(new WeeklyChallengeRevoked(challenge, s.PlayerId("Вася"), run, 15), Assert.Single(s.LastEvents<WeeklyChallengeRevoked>()));
        Assert.Empty(s.Completers(challenge));
    }

    [Fact]
    public void The_frozen_first_finisher_is_marked_without_coins()
    {
        var s = WeeklyChallengeScenario.New(r => r with { Finish = r.Finish with { RequireApprovalForFirst = false } }, mapLength: 2);
        var challenge = s.SetChallenge(WeeklyChallengeScenario.Manual());
        s.CompleteTitle("Вася", "Alan Wake");
        var run = s.CompleteTitle("Вася", "Outlast");

        s.Mark(challenge, "Вася", run);

        Assert.Equal(0, Assert.Single(s.LastEvents<WeeklyChallengeCompleted>()).Coins);
        Assert.DoesNotContain(s.LastEvents<CoinsChanged>(), c => c.Reason == CoinsReason.WeeklyChallenge);
    }

    [Theory]
    [InlineData("twice", RejectionCodes.WeeklyChallengeAlreadyCompleted)]
    [InlineData("twiceOtherRun", RejectionCodes.WeeklyChallengeAlreadyCompleted)]
    [InlineData("beforeWindow", RejectionCodes.WeeklyChallengeOutsideWindow)]
    [InlineData("afterWindow", RejectionCodes.WeeklyChallengeOutsideWindow)]
    [InlineData("auto", RejectionCodes.WeeklyChallengeNotManual)]
    [InlineData("unknownChallenge", RejectionCodes.WeeklyChallengeUnknown)]
    [InlineData("unknownRun", RejectionCodes.RunUnknown)]
    [InlineData("unknownPlayer", RejectionCodes.PlayerUnknown)]
    [InlineData("othersRun", RejectionCodes.NotYourRun)]
    [InlineData("playing", RejectionCodes.RunNotCompleted)]
    [InlineData("rejected", RejectionCodes.RunNotCompleted)]
    public void Marking_is_refused_when_the_rules_do_not_allow_it(string what, string code)
    {
        var s = WeeklyChallengeScenario.New();
        var start = s.Clock.UtcNow.AddDays(1);
        var early = s.CompleteTitle("Вася", "Dead Space"); // completed before the window
        var challenge = s.SetChallenge(WeeklyChallengeScenario.Manual(), start);
        var auto = s.SetChallenge(WeeklyChallengeScenario.HardWeek(), start);
        s.Advance(TimeSpan.FromDays(1));
        var run = s.CompleteTitle("Вася", "Alan Wake");
        var (player, target, onRun) = ("Вася", challenge, run);
        switch (what)
        {
            case "twice":
                s.Mark(challenge, "Вася", run);
                break;
            case "twiceOtherRun":
                s.Mark(challenge, "Вася", run);
                onRun = s.CompleteTitle("Вася", "Outlast");
                break;
            case "beforeWindow":
                onRun = early;
                break;
            case "afterWindow":
                s.Play("Вася", "Outlast");
                s.Advance(TimeSpan.FromDays(WeeklyChallengeScenario.WindowDays) + TimeSpan.FromMinutes(1));
                s.NextRandom(1, 1).Complete("Вася");
                onRun = s.State.Runs.Values.Single(r => r.PlayerId == s.PlayerId("Вася") && r.GameId == s.GameId("Outlast")).RunId;
                break;
            case "auto":
                target = auto; // a normal run does not meet «hard-week»: only the kind of check refuses
                break;
            case "unknownChallenge":
                target = Guid.Parse("00000000-0000-0000-0000-00000000c0de");
                break;
            case "unknownRun":
                onRun = Guid.Parse("00000000-0000-0000-0000-00000000beef");
                break;
            case "othersRun":
                player = "Петя";
                break;
            case "playing":
                player = "Петя";
                onRun = s.Play("Петя", "Outlast");
                break;
            case "rejected":
                s.Act(new RejectProof(run, "Не та игра"));
                break;
            default:
                break;
        }

        if (what == "unknownPlayer")
        {
            ScenarioAssert.RejectsWithoutChanges(
                s, x => x.Act(new MarkWeeklyChallenge(target, Guid.Parse("00000000-0000-0000-0000-0000000f00d5"), onRun)), code);
            return;
        }

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Mark(target, player, onRun), code);
    }

    // ---- Setting a challenge ----

    [Fact]
    public void Several_challenges_may_run_at_once()
    {
        var s = WeeklyChallengeScenario.New();

        var first = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var second = s.SetChallenge(WeeklyChallengeScenario.Manual());

        Assert.NotEqual(first, second);
        Assert.Equal(2, s.State.WeeklyChallenges.Count);
    }

    [Fact]
    public void An_invalid_definition_is_refused()
    {
        var s = WeeklyChallengeScenario.New();
        var invalid = WeeklyChallengeScenario.OldSchool() with { Reward = new RewardSpec { Coins = 0 } };

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Act(new SetWeeklyChallenge(invalid, x.Clock.UtcNow)), RejectionCodes.ContentInvalid);
    }

    // ---- S5.13-11: the flag (EV35) ----

    [Fact]
    public void With_the_flag_off_setting_a_challenge_is_refused()
    {
        var s = WeeklyChallengeScenario.New(r => r with { Features = r.Features with { WeeklyChallenge = false } });

        ScenarioAssert.RejectsWithoutChanges(
            s, x => x.Act(new SetWeeklyChallenge(WeeklyChallengeScenario.OldSchool(), x.Clock.UtcNow)), RejectionCodes.FeatureDisabled);
    }

    [Fact]
    public void With_the_flag_switched_off_marking_is_refused_and_completions_are_not_checked()
    {
        // A mechanic switched off mid-season freezes (D-420, D-528): no events, the challenge stays as it was
        var s = WeeklyChallengeScenario.New();
        var oldSchool = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var manual = s.SetChallenge(WeeklyChallengeScenario.Manual());
        s.WithRuleset(r => r with { Features = r.Features with { WeeklyChallenge = false } });

        var run = s.CompleteTitle("Вася", "Silent Hill 3");
        Assert.Empty(s.LastEvents<WeeklyChallengeCompleted>());
        Assert.Equal(0, ChallengeCoins(s, "Вася"));

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Mark(manual, "Вася", run), RejectionCodes.FeatureDisabled);
        Assert.Empty(s.Completers(oldSchool));
    }

    [Fact]
    public void Switched_back_on_the_challenge_counts_the_completions_from_then()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        s.WithRuleset(r => r with { Features = r.Features with { WeeklyChallenge = false } });
        s.CompleteTitle("Вася", "Silent Hill 3");
        s.WithRuleset(r => r with { Features = r.Features with { WeeklyChallenge = true } });
        Assert.Empty(s.LastEvents<WeeklyChallengeCompleted>());

        s.CompleteTitle("Петя", "Doom 3");

        Assert.Equal([s.PlayerId("Петя")], s.Completers(challenge));
    }

    [Fact]
    public void A_completion_with_the_flag_off_writes_what_it_writes_without_the_mechanic()
    {
        // Invariant 19: a challenge set before the flag went off adds nothing to a completion
        var plain = Scenario.New().WithCategory("Horror").WithReleasedGame("Silent Hill 3", 6, 2003, "Horror").WithPlayers("Вася");
        var off = WeeklyChallengeScenario.New();
        off.SetChallenge(WeeklyChallengeScenario.OldSchool());
        off.WithRuleset(r => r with { Features = r.Features with { WeeklyChallenge = false } });

        plain.CompleteTitle("Вася", "Silent Hill 3");
        off.CompleteTitle("Вася", "Silent Hill 3");

        Assert.Equal(plain.LastTypes(), off.LastTypes());
        Assert.Equal(
            plain.Last.Events.OfType<CoinsChanged>().Select(c => (c.Delta, c.Reason)),
            off.Last.Events.OfType<CoinsChanged>().Select(c => (c.Delta, c.Reason)));
    }

    // ---- The chain limit does not cut the calculation (D-413) ----

    // Every completion makes 60 notes in the log: past the 50 events of a command
    private sealed class Flood : ITriggerHandler
    {
        public IEnumerable<IGameEvent> React(SeasonState state, IGameEvent trigger, EngineContext context) =>
            trigger is RunCompleted completed
                ? Enumerable.Repeat<IGameEvent>(new PlayerAdjusted(completed.PlayerId, "Шум"), 60)
                : [];
    }

    [Fact]
    public void A_completion_whose_effects_hit_the_chain_limit_still_completes_the_challenge()
    {
        var s = WeeklyChallengeScenario.New();
        var challenge = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        s.WithTriggers(new Flood());

        s.CompleteTitle("Вася", "Silent Hill 3");

        Assert.Contains(s.Last.Events, e => e is EffectChainCut);
        Assert.Single(s.LastEvents<WeeklyChallengeCompleted>());
        Assert.Equal([s.PlayerId("Вася")], s.Completers(challenge));
    }

    // ---- Determinism ----

    [Fact]
    public void Replaying_the_log_gives_the_same_challenges()
    {
        var s = WeeklyChallengeScenario.New();
        var oldSchool = s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        var manual = s.SetChallenge(WeeklyChallengeScenario.Manual());
        var vasya = s.CompleteTitle("Вася", "Silent Hill 3");
        var petya = s.CompleteTitle("Петя", "Doom 3");
        s.Mark(manual, "Петя", petya);
        s.Act(new RejectProof(vasya, "Нет титров"));
        s.CompleteTitle("Маша", "Thief");

        var replayed = SeasonEngine.Replay(s.Log);

        Assert.Equal(s.State, replayed);
        Assert.Equal(s.State.WeeklyChallenges, replayed.WeeklyChallenges);
        Assert.Equal(new[] { s.PlayerId("Петя"), s.PlayerId("Маша") }.Order(), s.Completers(oldSchool).Order());
    }

    [Fact]
    public void The_same_commands_give_the_same_log()
    {
        static Scenario Play()
        {
            var s = WeeklyChallengeScenario.New();
            s.SetChallenge(WeeklyChallengeScenario.OldSchool());
            var run = s.CompleteTitle("Вася", "Silent Hill 3");
            s.Act(new RejectProof(run, "Нет титров"));
            s.CompleteTitle("Вася", "Doom 3");
            return s;
        }

        var first = Play();
        var second = Play();

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }
}
