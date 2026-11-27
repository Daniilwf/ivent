using GameEvent.Engine.Gallery;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Gallery;

public sealed class GalleryMomentTests
{
    private static readonly Guid s_firstItem = SequentialIds.Make(0x7C100000, 1);
    private static readonly Guid s_secondItem = SequentialIds.Make(0x7C100000, 2);
    private static readonly Guid s_thirdItem = SequentialIds.Make(0x7C100000, 3);
    private static readonly Guid s_firstFile = SequentialIds.Make(0x7C200000, 1);
    private static readonly Guid s_secondFile = SequentialIds.Make(0x7C200000, 2);
    private static readonly Guid s_thirdFile = SequentialIds.Make(0x7C200000, 3);
    private static readonly string s_checksum = new('A', 64);

    [Fact]
    public void A_publication_requires_an_enabled_live_season_a_current_participant_and_a_prepared_image()
    {
        var s = WithGallery().AsDraft().WithPlayers("Player");
        var player = s.Player("Player");
        var publish = Post(s_firstItem, player.UserId);
        Rejects(s, publish, s_firstFile, RejectionCodes.SeasonNotActive);

        s.MoveStatusTo(SeasonStatus.Active);
        Rejects(s, publish with { UserId = Guid.Empty }, s_firstFile, GalleryRejectionCodes.NotParticipant);
        Rejects(s, publish with { ItemId = Guid.Empty }, s_firstFile, GalleryRejectionCodes.InvalidItemId);
        Rejects(s, publish, null, GalleryRejectionCodes.InvalidImage);
        Rejects(s, new PublishGalleryMoment(s_firstItem, player.UserId, "Screenshot", "not a SHA-256"),
            s_firstFile, GalleryRejectionCodes.InvalidImage);
        Rejects(s, Post(s_firstItem, player.UserId, new string('a', Limits.MaxCommentLength + 1)),
            s_firstFile, GalleryRejectionCodes.CaptionTooLong);

        s.Act(new SetPlayerInactive(player.PlayerId, IsInactive: true));
        ScenarioAssert.Accepted(s);
        Rejects(s, publish, s_firstFile, GalleryRejectionCodes.NotParticipant);

        s.WithRuleset(r => r with { Features = r.Features with { Gallery = false } });
        Rejects(s, publish, s_firstFile, RejectionCodes.FeatureDisabled);
        s.Act(new HideGalleryMoment(s_firstItem));
        Assert.Equal(RejectionCodes.FeatureDisabled, s.Last.Rejection?.Code);
    }

    [Fact]
    public void A_normalized_command_does_not_depend_on_a_prepared_file_id()
    {
        var s = WithGallery().WithPlayers("Player");
        var player = s.Player("Player");
        var raw = new PublishGalleryMoment(s_firstItem, player.UserId, " \n Screenshot   ", s_checksum.ToLowerInvariant());
        var normalized = Post(s_firstItem, player.UserId);
        Assert.Equal(raw, normalized);
        Assert.Equal(
            Post(s_firstItem, player.UserId, caption: null),
            Post(s_firstItem, player.UserId, caption: "   "));

        var result = Execute(s, raw, s_firstFile);
        Assert.True(result.IsAccepted, result.Rejection?.Detail);
        Assert.Equal(
            new GalleryMomentPublished(s_firstItem, player.UserId, player.PlayerId, s_firstFile, "Screenshot",
                s.Clock.UtcNow, new DateOnly(2026, 10, 1)),
            Assert.Single(result.Events));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\n")]
    public void A_publication_requires_a_nonblank_caption(string? caption)
    {
        var s = WithGallery().WithPlayers("Player");
        Rejects(s, Post(s_firstItem, s.Player("Player").UserId, caption), s_firstFile, GalleryRejectionCodes.CaptionRequired);
    }

    [Fact]
    public void The_daily_debit_is_per_account_and_local_day_and_survives_hiding()
    {
        var s = WithGallery(limit: 1).WithPlayers("Player", "Other")
            .Advance(TimeSpan.FromHours(9) - TimeSpan.FromSeconds(1));
        var player = s.Player("Player");
        var other = s.Player("Other");
        AppendPublication(s, Post(s_firstItem, player.UserId), s_firstFile);
        s.Act(new HideGalleryMoment(s_firstItem));
        ScenarioAssert.Accepted(s);

        var hidden = s.State.GalleryItems[s_firstItem];
        Assert.True(hidden.Hidden);
        Assert.Equal(new DateOnly(2026, 10, 1), hidden.CreatedDay);

        Rejects(s, Post(s_firstItem, player.UserId), s_secondFile, GalleryRejectionCodes.ItemAlreadyExists);
        Rejects(s, Post(s_secondItem, other.UserId), s_firstFile, GalleryRejectionCodes.InvalidImage);
        Rejects(s, Post(s_secondItem, player.UserId), s_secondFile, GalleryRejectionCodes.DailyLimit);
        AppendPublication(s, Post(s_secondItem, other.UserId), s_secondFile);

        s.Advance(TimeSpan.FromSeconds(1));
        AppendPublication(s, Post(s_thirdItem, player.UserId), s_thirdFile);
        Assert.Equal(new DateOnly(2026, 10, 2), s.State.GalleryItems[s_thirdItem].CreatedDay);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void Prior_uploads_count_under_the_current_season_time_zone()
    {
        var s = WithGallery(limit: 1)
            .WithRuleset(r => r with { Season = r.Season with { Timezone = "UTC" } })
            .WithPlayers("Player")
            .Advance(TimeSpan.FromHours(11.5));
        var player = s.Player("Player");
        AppendPublication(s, Post(s_firstItem, player.UserId), s_firstFile);
        Assert.Equal(new DateOnly(2026, 10, 1), s.State.GalleryItems[s_firstItem].CreatedDay);

        s.WithRuleset(r => r with { Season = r.Season with { Timezone = "Europe/Moscow" } });
        Rejects(s, Post(s_secondItem, player.UserId), s_secondFile, GalleryRejectionCodes.DailyLimit);

        s.Advance(TimeSpan.FromHours(21) + TimeSpan.FromMinutes(30));
        AppendPublication(s, Post(s_secondItem, player.UserId), s_secondFile);
        Assert.Equal(new DateOnly(2026, 10, 3), s.State.GalleryItems[s_secondItem].CreatedDay);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void Undo_restores_an_items_visibility_and_debit_without_holding_unrelated_gameplay()
    {
        var s = WithGallery(limit: 2).WithPlayers("Player");
        var player = s.Player("Player");
        var enrollment = s.LastCommandId;
        s.Act(new AdjustPlayer(player.PlayerId, "Adjustment", PointsDelta: 5));
        ScenarioAssert.Accepted(s);
        var adjustment = s.LastCommandId;
        var first = AppendPublication(s, Post(s_firstItem, player.UserId), s_firstFile);
        var second = AppendPublication(s, Post(s_secondItem, player.UserId), s_secondFile);
        var beforeHide = s.State.GalleryItems[s_firstItem];
        s.Act(new HideGalleryMoment(s_firstItem));
        ScenarioAssert.Accepted(s);
        var hide = s.LastCommandId;

        s.Act(new UndoCommand(first, "Undo the publication"));
        var dependentHide = Assert.IsType<Rejection>(s.Last.Rejection);
        Assert.Equal(RejectionCodes.UndoDependents, dependentHide.Code);
        Assert.Equal(hide, Assert.Single(dependentHide.Related));

        s.Act(new UndoCommand(adjustment, "Undo the points adjustment"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(player.Points, s.Player("Player").Points);

        s.Act(new UndoCommand(enrollment, "Undo the enrollment"));
        var dependentPosts = Assert.IsType<Rejection>(s.Last.Rejection);
        Assert.Equal(RejectionCodes.UndoDependents, dependentPosts.Code);
        Assert.Equal(new[] { first, second }, dependentPosts.Related);

        s.Act(new UndoCommand(hide, "Undo hiding the moment"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(beforeHide, s.State.GalleryItems[s_firstItem]);
        s.Act(new UndoCommand(first, "Undo the publication"));
        ScenarioAssert.Accepted(s);
        Assert.Equal(s_secondItem, Assert.Single(s.State.GalleryItems).Key);
        AppendPublication(s, Post(s_thirdItem, player.UserId), s_thirdFile);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Fact]
    public void Reactivation_cannot_be_undone_while_a_later_publication_depends_on_it()
    {
        var s = WithGallery().WithPlayers("Player", "Other");
        var player = s.Player("Player");
        var other = s.Player("Other");
        s.Act(new SetPlayerInactive(player.PlayerId, IsInactive: true));
        ScenarioAssert.Accepted(s);
        s.Act(new SetPlayerInactive(player.PlayerId, IsInactive: false));
        ScenarioAssert.Accepted(s);
        var reactivation = s.LastCommandId;
        var dependent = AppendPublication(s, Post(s_firstItem, player.UserId), s_firstFile);
        AppendPublication(s, Post(s_secondItem, other.UserId), s_secondFile);

        s.Act(new UndoCommand(reactivation, "Undo reactivation"));
        var blocked = Assert.IsType<Rejection>(s.Last.Rejection);
        Assert.Equal(RejectionCodes.UndoDependents, blocked.Code);
        Assert.Equal(dependent, Assert.Single(blocked.Related));

        s.Act(new UndoCommand(dependent, "Withdraw the moment"));
        ScenarioAssert.Accepted(s);
        s.Act(new UndoCommand(reactivation, "Undo reactivation"));
        ScenarioAssert.Accepted(s);
        Assert.True(s.Player("Player").IsInactive);
        Assert.Equal(s_secondItem, Assert.Single(s.State.GalleryItems).Key);
        Assert.Equal(s.State, SeasonEngine.Replay(s.StoredLog));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Later_activity_changes_do_not_hold_the_undo_of_a_prior_publication(bool reactivate)
    {
        var s = WithGallery().WithPlayers("Player");
        var player = s.Player("Player");
        var prior = AppendPublication(s, Post(s_firstItem, player.UserId), s_firstFile);
        s.Act(new SetPlayerInactive(player.PlayerId, IsInactive: true));
        ScenarioAssert.Accepted(s);
        if (reactivate)
        {
            s.Act(new SetPlayerInactive(player.PlayerId, IsInactive: false));
            ScenarioAssert.Accepted(s);
        }

        s.Act(new UndoCommand(prior, "Withdraw the moment"));
        ScenarioAssert.Accepted(s);
        Assert.Empty(s.State.GalleryItems);
        Assert.Equal(!reactivate, s.Player("Player").IsInactive);
    }

    [Fact]
    public void Closing_still_accepts_moments_and_an_archive_can_be_moderated()
    {
        var s = WithGallery().WithPlayers("Player").MoveStatusTo(SeasonStatus.Closing);
        var player = s.Player("Player");
        AppendPublication(s, Post(s_firstItem, player.UserId), s_firstFile);
        s.MoveStatusTo(SeasonStatus.Archived);
        var standings = s.State.Result;
        Assert.NotNull(standings);

        Rejects(s, Post(s_secondItem, player.UserId), s_secondFile, RejectionCodes.SeasonClosed);
        s.Act(new HideGalleryMoment(s_firstItem));
        ScenarioAssert.Accepted(s);
        Assert.True(s.State.GalleryItems[s_firstItem].Hidden);
        Assert.Equal(standings, s.State.Result);
    }

    private static Scenario WithGallery(int limit = 10) =>
        Scenario.New().WithRuleset(r => r with
        {
            Features = r.Features with { Gallery = true },
            Social = r.Social with { GalleryUploadsPerDay = limit },
        });

    private static PublishGalleryMoment Post(Guid itemId, Guid userId, string? caption = "Screenshot") =>
        new(itemId, userId, caption, s_checksum);

    private static CommandResult Execute(Scenario s, PublishGalleryMoment command, Guid? fileId) =>
        SeasonEngine.Execute(s.State, command, s.Context() with { PreparedGalleryFileId = fileId });

    private static Guid AppendPublication(Scenario s, PublishGalleryMoment command, Guid fileId)
    {
        var result = Execute(s, command, fileId);
        Assert.True(result.IsAccepted, result.Rejection?.Detail);
        s.AppendCraftedEvents([.. result.Events]);
        Assert.Equal(result.State, s.State);
        return s.History[^1].CommandId;
    }

    private static void Rejects(Scenario s, PublishGalleryMoment command, Guid? fileId, string code)
    {
        var result = Execute(s, command, fileId);
        Assert.Equal(code, result.Rejection?.Code);
        Assert.Empty(result.Events);
        Assert.Equal(s.State, result.State);
    }
}
