using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Polls;

/// <summary>
/// The thin slice of polls (task 5.8): the admin starts the CONTENT.md poll «жанр лидеру», players vote, the timer closes it
/// and the leader gets the voted genre as a special roll (SPEC «Голосования», D-512…D-514).
/// </summary>
public class PollSliceTests
{
    [Fact]
    public void The_admin_poll_closes_by_the_timer_and_gives_the_leader_the_voted_genre()
    {
        var s = Scenario.New(TestRuleset.Create().WithPolls())
            .WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 8, "Horror").WithGame("Skyrim", 30, "RPG")
            .WithPlayers("Вася", "Петя", "Маша")
            .WithContent(EconomyScenario.ExamplePack("forced-genre"));
        s.Act(new Engine.Players.AdjustPlayer(s.PlayerId("Петя"), "Лидер", PointsDelta: 5));

        s.Act(new StartPoll(new PollDefinition
        {
            Question = "Какой жанр подкинем лидеру?",
            Options = new ChoiceOptionsSpec { From = ChoiceSource.Categories },
            Voters = PollVoters.Players,
            Anonymous = true,
            ClosesInHours = 24,
            OnResult = new EffectSpec
            {
                Target = new TargetSpec { Selector = TargetSelector.Leader },
                Actions = [new GiveObjectAction { ObjectId = "forced-genre", Params = new ContentParamDictionary([new("tag", "$result")]) }],
            },
        }));
        var poll = Assert.Single(s.LastEvents<PollStarted>());
        Assert.Equal(["Horror", "RPG"], poll.Options.Select(o => o.Id));

        s.Act(new CastVote(poll.PollId, s.Player("Вася").UserId, "Horror"));
        s.Act(new CastVote(poll.PollId, s.Player("Маша").UserId, "Horror"));
        s.Act(new CastVote(poll.PollId, s.Player("Петя").UserId, "RPG"));
        Assert.True(s.Last.IsAccepted);

        s.Advance(TimeSpan.FromHours(24)).Act(new FireTimers());

        var closed = Assert.Single(s.LastEvents<PollClosed>());
        Assert.Equal("Horror", closed.Result);
        var given = Assert.Single(s.LastEvents<ObjectGiven>());
        Assert.Equal(s.PlayerId("Петя"), given.PlayerId);
        Assert.Equal("Horror", given.Item.Params!["tag"]);
    }
}
