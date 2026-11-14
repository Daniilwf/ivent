using System.Globalization;
using System.Text;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Challenges;
using GameEvent.Engine.Tests.Coop;
using GameEvent.Engine.Tests.GlobalEvents;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;
using GameEvent.Engine.Undo;
using DuelScenario = GameEvent.Engine.Tests.Duels.DuelScenario;
using PollSetup = GameEvent.Engine.Tests.Polls.PollSetup;

namespace GameEvent.Engine.Tests.Snapshots;

/// <summary>
/// Stage 5 (task 5.15, EV49, invariant 14): the whole log of key scenarios as stored (Verify, like
/// <see cref="LogSnapshotTests"/>, D-112) — the drawn event that asks and its answer; a karaoke from a drop with its IRL
/// proof approved; a poll with an effect on everyone closed and settled; a global goal reached and its reward settled; a coop
/// run split between two, rejected, the reject undone; the weekly challenge done by both in coop; a duel won by the first
/// completion. A change of what any of them stores fails here until the snapshot is reviewed. Written by
/// <c>test-author</c> from the SPEC.
/// </summary>
public class EventsLogSnapshotTests
{
    static EventsLogSnapshotTests()
    {
        DiffEngine.DiffRunner.Disabled = true;
    }

    private static readonly ContentPack s_pack = GlobalSetup.Pack(
        null,
        EventScenario.Deck(EventKind.Risky, "risky", "coin-or-step"),
        EventScenario.Deck(EventKind.Good, "good", "tailwind"),
        EventScenario.Deck(EventKind.Bad, "bad", "karaoke"));

    [Fact]
    public Task An_event_drawn_on_a_cell_asks_and_is_answered()
    {
        // start → c1 → e2 (an event cell, the default deck) → … ; a 6-hour game throws 2d4: 1 + 1 stops on e2
        var map = MapBuilder.New().EventCell("e2").Path("start", "c1", "e2", "c3", "c4", "c5", "c6", "finish").Build();
        var s = Scenario.New(TestRuleset.Create().WithEvents()).WithMap(map).WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася");
        s.WithContent(s_pack);
        s.RollTitle("Вася", "Silent Hill").Start("Вася").NextRandom(1, 1, 0).Complete("Вася");
        ScenarioAssert.Accepted(s);
        var choice = s.Player("Вася").Choice!;
        s.Act(new MakeChoice(s.PlayerId("Вася"), choice.ChoiceId, choice.Options[0].Id));
        ScenarioAssert.Accepted(s);

        return Verifier.Verify(Render(s));
    }

    [Fact]
    public Task A_karaoke_from_a_drop_with_its_irl_proof_approved()
    {
        var s = Scenario.New(TestRuleset.Create().WithEvents()).WithMapLength(12).WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася");
        s.WithContent(s_pack);
        s.RollTitle("Вася", "Silent Hill").Start("Вася");
        s.Advance(TimeSpan.FromHours(2));
        s.Act(new Engine.Runs.DropRun(s.PlayerId("Вася")));
        ScenarioAssert.Accepted(s);
        var effect = Assert.Single(s.State.ManualEffects.Values);
        s.Act(new SubmitIrlProof(s.PlayerId("Вася"), effect.EffectId, [], "https://video.example/karaoke"));
        ScenarioAssert.Accepted(s);
        s.Act(new ReviewIrlProof(effect.EffectId, Approved: true, "Поёт"));
        ScenarioAssert.Accepted(s);

        return Verifier.Verify(Render(s));
    }

    [Fact]
    public Task A_poll_with_an_effect_on_everyone_closed_and_settled()
    {
        var s = PollSetup.Season();
        var poll = PollSetup.Start(s, PollSetup.EffectPoll(GlobalSetup.OnAll(GlobalSetup.Coins(2))));
        PollSetup.Voted(s, poll.PollId, "Вася", poll.Options[0].Id);
        PollSetup.Voted(s, poll.PollId, "Петя", poll.Options[0].Id);
        PollSetup.Voted(s, poll.PollId, "Маша", poll.Options[1].Id);
        s.Act(new ClosePoll(poll.PollId));
        ScenarioAssert.Accepted(s);
        PollSetup.SettleAll(s);

        return Verifier.Verify(Render(s));
    }

    [Fact]
    public Task A_global_goal_reached_and_its_reward_settled()
    {
        var s = GlobalSetup.Season();
        s.Launch("coin-rain", 72, GlobalSetup.RunsGoal(1, GlobalSetup.OnAll(GlobalSetup.Coins(1))));
        GlobalSetup.SettleAll(s);
        s.Completed("Вася", "Alan Wake");
        ScenarioAssert.Accepted(s);
        GlobalSetup.SettleAll(s);

        return Verifier.Verify(Render(s));
    }

    [Fact]
    public Task A_coop_run_split_between_two_rejected_and_the_reject_undone()
    {
        var s = CoopTestScenario.New();
        var run = s.CoopRun();
        s.CompleteWith("Вася", 3, 1);
        s.Act(new RejectProof(run, "На скрине другая игра"));
        ScenarioAssert.Accepted(s);
        s.Act(new UndoCommand(s.LastCommandId, "Ошибка админа"));
        ScenarioAssert.Accepted(s);

        return Verifier.Verify(Render(s));
    }

    [Fact]
    public Task The_weekly_challenge_done_by_both_in_coop()
    {
        var s = WeeklyChallengeScenario.New(r => r.WithCoop());
        s.SetChallenge(WeeklyChallengeScenario.OldSchool());
        s.RollTitle("Вася", "Silent Hill 3").Start("Вася");
        s.Act(new Engine.Interactions.InviteToCoop(s.PlayerId("Вася"), s.PlayerId("Петя")));
        ScenarioAssert.Accepted(s);
        var invitation = Assert.Single(s.State.Interactions.Values).InteractionId;
        s.Act(new Engine.Interactions.AnswerInvitation(s.PlayerId("Петя"), invitation, Accept: true));
        ScenarioAssert.Accepted(s);
        s.NextRandom(1, 1).Complete("Вася");
        ScenarioAssert.Accepted(s);

        return Verifier.Verify(Render(s));
    }

    [Fact]
    public Task A_duel_won_by_the_first_completion()
    {
        var s = DuelScenario.New();
        DuelScenario.StartDuel(s, "Вася", "Петя");
        DuelScenario.CompleteTitle(s, "Вася", "Silent Hill");
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

                // Commands that carry content print by name: the content is in their events
                text.Append("# ").Append(command switch
                {
                    null => "(crafted)",
                    CreateSeason or PublishContent or StartPoll or Engine.Challenges.SetWeeklyChallenge or Engine.GlobalEvents.LaunchGlobalEvent => command.GetType().Name,
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
