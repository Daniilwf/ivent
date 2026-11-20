using FsCheck.Xunit;
using GameEvent.Engine.Gallery;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Reactions;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Reactions;

public sealed class GalleryReactionTests
{
    private const string Fire = "🔥";
    private const string Laugh = "😂";
    private static readonly Guid s_moment = SequentialIds.Make(0x7C300000, 1);
    private static readonly Guid s_otherMoment = SequentialIds.Make(0x7C300000, 2);
    private static readonly Guid s_spectator = SequentialIds.Make(0x7C400000, 1);

    [Fact]
    public void Gallery_reactions_require_both_features_a_visible_moment_and_a_configured_emoji()
    {
        var s = WithGallery(reactions: false);
        Publish(s, s_moment);
        var choose = new SetGalleryReaction(s_moment, s_spectator, Fire, Selected: true);
        Rejects(s, choose, RejectionCodes.FeatureDisabled);

        s.WithRuleset(r => r with { Features = r.Features with { Reactions = true, Gallery = false } });
        Rejects(s, choose, RejectionCodes.FeatureDisabled);
        s.WithRuleset(r => r with { Features = r.Features with { Gallery = true } });

        Rejects(s, choose with { ItemId = s_otherMoment }, GalleryRejectionCodes.TargetUnknown);
        Rejects(s, choose with { UserId = Guid.Empty }, "galleryReaction.invalidUser");
        Rejects(s, choose with { Emoji = Fire + " " }, "galleryReaction.invalidEmoji");

        s.Act(new HideGalleryMoment(s_moment));
        ScenarioAssert.Accepted(s);
        Rejects(s, choose, GalleryRejectionCodes.TargetUnknown);
        Rejects(s, choose with { Selected = false }, GalleryRejectionCodes.TargetUnknown);
    }

    [Fact]
    public void Gallery_reactions_depend_on_the_publication_and_the_same_choice_only()
    {
        var s = WithGallery();
        var player = s.Player("Player");
        s.Act(new AdjustPlayer(player.PlayerId, "Adjustment", PointsDelta: 5));
        ScenarioAssert.Accepted(s);
        var adjustment = s.LastCommandId;
        var published = Publish(s, s_moment);
        Publish(s, s_otherMoment);

        var choose = new SetGalleryReaction(s_moment, s_spectator, Fire, Selected: true);
        var original = React(s, choose);
        var confirmation = React(s, choose);
        var otherAccount = React(s, choose with { UserId = player.UserId });
        var otherEmoji = React(s, choose with { Emoji = Laugh });
        React(s, choose with { ItemId = s_otherMoment });
        Assert.Equal(4, s.State.GalleryReactions.Count);

        BlockedUndo(s, original, confirmation);
        BlockedUndo(s, published, original, confirmation, otherAccount, otherEmoji);
        Undo(s, adjustment);
        Assert.Equal(player.Points, s.Player("Player").Points);

        Undo(s, confirmation);
        Undo(s, original);
        Assert.True(s.State.GalleryReactions.SetEquals([
            new GalleryReaction(s_moment, player.UserId, Fire),
            new GalleryReaction(s_moment, s_spectator, Laugh),
            new GalleryReaction(s_otherMoment, s_spectator, Fire),
        ]));

        Undo(s, otherAccount);
        Undo(s, otherEmoji);
        Undo(s, published);
        Assert.False(s.State.GalleryItems.ContainsKey(s_moment));
        Assert.Equal(new GalleryReaction(s_otherMoment, s_spectator, Fire), Assert.Single(s.State.GalleryReactions));
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void Undoing_moderation_restores_the_visible_moment_with_its_prior_reactions()
    {
        var s = WithGallery();
        Publish(s, s_moment);
        var choose = new SetGalleryReaction(s_moment, s_spectator, Fire, Selected: true);
        React(s, choose);

        s.Act(new HideGalleryMoment(s_moment));
        ScenarioAssert.Accepted(s);
        var hide = s.LastCommandId;
        Assert.True(s.State.GalleryItems[s_moment].Hidden);
        Rejects(s, choose with { Selected = false }, GalleryRejectionCodes.TargetUnknown);

        Undo(s, hide);
        Assert.False(s.State.GalleryItems[s_moment].Hidden);
        Assert.Equal(new GalleryReaction(s_moment, s_spectator, Fire), Assert.Single(s.State.GalleryReactions));
        var removal = React(s, choose with { Selected = false });
        Assert.Empty(s.State.GalleryReactions);
        Undo(s, removal);
        Assert.Equal(new GalleryReaction(s_moment, s_spectator, Fire), Assert.Single(s.State.GalleryReactions));
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void An_archived_visible_moment_can_receive_reactions_without_changing_the_result()
    {
        var s = WithGallery();
        Publish(s, s_moment);
        s.MoveStatusTo(SeasonStatus.Archived);
        var standings = s.State.Result;
        Assert.NotNull(standings);

        var choose = new SetGalleryReaction(s_moment, s_spectator, Laugh, Selected: true);
        var reaction = React(s, choose);
        Assert.Equal(new GalleryReaction(s_moment, s_spectator, Laugh), Assert.Single(s.State.GalleryReactions));
        s.Act(new UndoCommand(reaction, "Correction"));
        Assert.Equal(RejectionCodes.SeasonClosed, s.Last.Rejection?.Code);

        React(s, choose with { Selected = false });
        Assert.Empty(s.State.GalleryReactions);
        Assert.Equal(standings, s.State.Result);
    }

    [Property(MaxTest = 80)]
    public void A_script_of_choices_keeps_the_last_state_for_each_moment_account_and_emoji(byte[] script)
    {
        var s = WithGallery();
        var player = s.Player("Player");
        Publish(s, s_moment);
        Publish(s, s_otherMoment);
        var expected = new HashSet<GalleryReaction>();

        foreach (var step in (script ?? []).Take(32))
        {
            var moment = (step & 1) == 0 ? s_moment : s_otherMoment;
            var account = (step & 2) == 0 ? s_spectator : player.UserId;
            var emoji = (step & 4) == 0 ? Fire : Laugh;
            var chosen = (step & 8) != 0;
            var reaction = new GalleryReaction(moment, account, emoji);
            React(s, new SetGalleryReaction(moment, account, emoji, chosen));
            if (chosen)
            {
                expected.Add(reaction);
            }
            else
            {
                expected.Remove(reaction);
            }

            Assert.True(s.State.GalleryReactions.SetEquals(expected));
        }

        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    private static Scenario WithGallery(bool reactions = true) => Scenario.New()
        .WithRuleset(r => r with
        {
            Features = r.Features with { Gallery = true, Reactions = reactions },
            Social = r.Social with { Reactions = [Fire, Laugh] },
        })
        .WithPlayers("Player");

    private static Guid Publish(Scenario s, Guid moment)
    {
        var command = new PublishGalleryMoment(moment, s.Player("Player").UserId, "Screenshot", new string('A', 64));
        var fileNumber = moment == s_moment ? 1 : 2;
        var fileId = SequentialIds.Make(0x7C500000, fileNumber);
        var result = SeasonEngine.Execute(s.State, command, s.Context() with { PreparedGalleryFileId = fileId });
        Assert.True(result.IsAccepted, result.Rejection?.Detail);
        s.AppendCraftedEvents([.. result.Events]);
        Assert.Equal(result.State, s.State);
        return s.History[^1].CommandId;
    }

    private static Guid React(Scenario s, SetGalleryReaction command)
    {
        s.Act(command);
        ScenarioAssert.Accepted(s);
        Assert.Equal(new GalleryReactionSet(command.ItemId, command.UserId, command.Emoji, command.Selected), Assert.Single(s.Last.Events));
        return s.LastCommandId;
    }

    private static void Rejects(Scenario s, SetGalleryReaction command, string code)
    {
        var before = s.State;
        s.Act(command);
        var rejection = Assert.IsType<Rejection>(s.Last.Rejection);
        Assert.Equal(code, rejection.Code);
        Assert.Empty(s.Last.Events);
        Assert.Equal(before, s.State);
    }

    private static void Undo(Scenario s, Guid commandId)
    {
        s.Act(new UndoCommand(commandId, "Correction"));
        ScenarioAssert.Accepted(s);
    }

    private static void BlockedUndo(Scenario s, Guid commandId, params Guid[] dependents)
    {
        s.Act(new UndoCommand(commandId, "Correction"));
        var rejection = Assert.IsType<Rejection>(s.Last.Rejection);
        Assert.Equal(RejectionCodes.UndoDependents, rejection.Code);
        Assert.Equal(dependents, rejection.Related);
    }
}
