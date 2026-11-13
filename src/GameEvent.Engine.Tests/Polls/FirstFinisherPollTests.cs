using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Tests.Events;
using GameEvent.Engine.Tests.Support;
using static GameEvent.Engine.Tests.Polls.PollSetup;

namespace GameEvent.Engine.Tests.Polls;

/// <summary>
/// The first finisher and polls (task 5.8; SPEC «Проверка на дыры» — «Финишировавший первым помогает или вредит», EV42;
/// D-513): frozen, he no longer acts on others, so he does not vote in a poll whose result acts on players (the admin's
/// <c>onResult</c> or an event that waits for it), provisional or approved; in a poll without an effect (a nomination) he
/// votes. The result never goes to him: the leader of a result is the leader among the others. Written by
/// <c>test-author</c> from the SPEC, before the implementation.
/// </summary>
public class FirstFinisherPollTests
{
    // ---- S5.8-09 (EV42): approved first finisher ----

    [Fact]
    public void The_approved_first_finisher_votes_in_a_nomination_but_not_in_a_poll_with_an_effect()
    {
        var s = Season(mapLength: 4);
        s.Approve(s.Finish("Маша"));
        Assert.True(s.Player("Маша").Finish!.Frozen);
        var withEffect = s.Start(ContentPoll());
        var nomination = s.Start(ListPoll());

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(withEffect.PollId, "Маша", "Horror"), RejectionCodes.PollFrozenVoter);
        s.Voted(nomination.PollId, "Маша", "Финиш");

        // The others vote in both
        s.Voted(withEffect.PollId, "Вася", "Horror").Voted(nomination.PollId, "Вася", "Дроп");
    }

    // ---- S5.8-10: the provisional first finisher (Н-2, default: refused — he «no longer acts on others») ----

    [Fact]
    public void The_provisional_first_finisher_does_not_vote_with_an_effect_until_his_finish_is_rejected()
    {
        var s = Season(mapLength: 4);
        var run = s.Finish("Маша");
        Assert.Equal(s.PlayerId("Маша"), FinishLine.First(s.State));
        var withEffect = s.Start(ContentPoll());

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(withEffect.PollId, "Маша", "Horror"), RejectionCodes.PollFrozenVoter);

        s.Act(new RejectProof(run, "Не та игра"));
        ScenarioAssert.Accepted(s);
        Assert.Null(s.Player("Маша").Finish);

        s.Voted(withEffect.PollId, "Маша", "Horror");
    }

    [Fact]
    public void A_vote_cast_before_the_finish_stays_and_cannot_be_changed_after_it()
    {
        // The vote of a player who finishes first while the poll is open: counted as cast (a vote in force is history);
        // a change after the finish would be a new act on others, so it is refused like a first vote
        var s = Season(mapLength: 4);
        var poll = s.Start(ContentPoll());
        s.Voted(poll.PollId, "Маша", "RPG");
        s.Finish("Маша");

        ScenarioAssert.RejectsWithoutChanges(s, x => x.Vote(poll.PollId, "Маша", "Horror"), RejectionCodes.PollFrozenVoter);
        Assert.Contains(new Vote(s.Player("Маша").UserId, "RPG"), s.State.Polls[poll.PollId].Votes);
    }

    [Fact]
    public void A_later_finisher_votes_in_a_poll_with_an_effect()
    {
        // Only the first is frozen (SPEC «После финиша первым»): a later finisher's points still grow and he still plays
        var s = Season(mapLength: 4);
        s.Finish("Маша");
        s.Finish("Вася");
        var poll = s.Start(ContentPoll());

        s.Voted(poll.PollId, "Вася", "Horror");
    }

    // ---- S5.8-24 (EV42): the leader of a result is not the first finisher ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_leader_of_a_result_is_the_leader_among_the_others(bool approved)
    {
        var s = Season(mapLength: 4);
        var run = s.Finish("Маша");
        if (approved)
        {
            s.Approve(run);
        }

        s.WithPoints("Маша", 100).WithPoints("Петя", 20).WithPoints("Вася", 5).WithPoints("Коля", 1);
        var poll = s.Start(ContentPoll());
        s.Voted(poll.PollId, "Вася", "Horror");

        s.CloseByTimer(poll.PollId);

        Assert.Equal(s.PlayerId("Петя"), Assert.Single(s.LastEvents<ObjectGiven>()).PlayerId);
        Assert.Empty(s.Genres("Маша"));
    }

    [Fact]
    public void A_result_on_everyone_skips_the_first_finisher()
    {
        var s = Season(mapLength: 4);
        s.Finish("Маша");
        var poll = s.Start(EffectPoll(new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.All }, Actions = [Coins(1)] }));
        s.Voted(poll.PollId, "Вася", "Horror");
        var masha = s.Player("Маша").Coins;

        s.CloseByTimer(poll.PollId);

        Assert.DoesNotContain(s.PlayerId("Маша"), Assert.Single(s.LastEvents<ForAllQueued>()).Players);
        s.SettleAll();
        Assert.Equal(masha, s.Player("Маша").Coins);
    }

    [Fact]
    public void A_result_on_a_random_active_player_never_reaches_the_first_finisher()
    {
        // Every seed: the random pick is among the others
        for (var seed = 0; seed < 12; seed++)
        {
            var s = Season(mapLength: 4, seed: seed);
            s.Finish("Маша");
            var poll = s.Start(ListEffectPoll(new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.RandomActive }, Actions = [Coins(3)] }));
            s.Voted(poll.PollId, "Вася", "да");
            var masha = s.Player("Маша").Coins;

            s.CloseByTimer(poll.PollId);

            ScenarioAssert.Accepted(s);
            Assert.Equal(masha, s.Player("Маша").Coins);
        }
    }
}
