using System.Collections.Immutable;
using System.Text.Json;
using GameEvent.Engine.Challenges;
using GameEvent.Engine.Content;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Suggestions;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Tests.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Queue;

/// <summary>
/// Task 5.16: the migration «Events» (only additive) and the projection of the stage 5 mechanics (SPEC «Модель данных»:
/// Poll, PollOption, Vote, GlobalEvent, Interaction, PartnerRequest, WeeklyChallenge, Suggestion; the run's partner and
/// conditions, the proof of a manual effect). Each table is written in the command's transaction and equals the fold of
/// the log; the integrity check compares it (L4) — a row changed behind the queue's back is a difference. Votes are unique
/// per poll and user in the database too (EV40). Written by <c>test-author</c> from the SPEC and the public contract of the
/// engine, before the implementation; the schema is read by plain SQL (D-810).
/// </summary>
public sealed class EventsProjectionTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- The migration ----

    [Fact]
    public async Task The_events_migration_is_applied_with_its_tables_and_columns()
    {
        await using var db = _site.NewDb();

        Assert.Contains(db.Database.GetAppliedMigrations(), m => m.EndsWith("_Events", StringComparison.Ordinal));
        foreach (var table in Tables)
        {
            Assert.True(await TableExistsAsync(db, table), $"Table {table} is missing.");
        }

        foreach (var (table, column) in Columns)
        {
            Assert.Contains(column, await ColumnsOfAsync(db, table));
        }
    }

    [Fact]
    public async Task The_events_migration_comes_on_top_of_the_released_ones()
    {
        // Only additive (CLAUDE.md «Миграции во время сезона только расширяющие»): a new migration after the last released one;
        // that it loses nothing and rolls back on the demo copy is npm run test:migrations
        await using var db = _site.NewDb();
        var all = db.Database.GetMigrations().ToList();

        var events = all.FindIndex(m => m.EndsWith("_Events", StringComparison.Ordinal));
        Assert.True(events >= 0, "The migration «Events» (5.16) is missing.");
        Assert.True(events > all.IndexOf("20261112064220_Sessions"), "The migration «Events» must come after the last released one.");
        Assert.Equal(all.Order(StringComparer.Ordinal), all);
    }

    [Fact]
    public async Task A_vote_is_unique_per_poll_and_user_in_the_database()
    {
        // EV40: one vote per user, whatever reaches the database
        await using var db = _site.NewDb();
        await AssertSchemaAsync(db, "Vote", "PollId", "UserId", "OptionId");
        var unique = await RowsAsync(
            db,
            """
            SELECT group_concat(ii.name, ',') FROM pragma_index_list('Vote') il JOIN pragma_index_info(il.name) ii
            WHERE il."unique" = 1 GROUP BY il.name
            """);

        Assert.Contains(unique, r => string.Equals((string?)r[0], "PollId,UserId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_vote_row_of_the_same_user_in_a_poll_is_refused_by_the_database()
    {
        await PollsOnAsync(_site);
        await _site.SendAsync(new StartPoll(Nomination()));
        var poll = Assert.Single((await StateAsync(_site)).Polls.Values);
        var vasya = _site.Users["vasya"];
        await _site.SendAsync(new CastVote(poll.PollId, vasya, "Финиш"));

        await using var db = _site.NewDb();
        await AssertSchemaAsync(db, "Vote", "PollId", "UserId", "OptionId");
        Assert.Single(await RowsAsync(db, "SELECT * FROM Vote"));
        var columns = await ColumnsOfAsync(db, "Vote");
        var copy = $"INSERT INTO Vote ({string.Join(", ", columns)}) SELECT {string.Join(", ", columns)} FROM Vote";

        var refused = await Assert.ThrowsAnyAsync<Microsoft.Data.Sqlite.SqliteException>(() => ExecuteAsync(db, copy));
        Assert.Contains("UNIQUE", refused.Message, StringComparison.Ordinal);
    }

    // ---- Polls, options and votes ----

    [Fact]
    public async Task Polls_their_options_and_votes_in_force_are_projected_equal_to_the_log()
    {
        await PollsOnAsync(_site);
        await _site.SendAsync(new StartPoll(Nomination()));
        await _site.SendAsync(new StartPoll(Nomination(anonymous: true) with { Question = "Анонимно?" }));
        var polls = (await StateAsync(_site)).Polls.Values.ToList();
        var (vasya, petya) = (_site.Users["vasya"], _site.Users["petya"]);
        await _site.SendAsync(new CastVote(polls[0].PollId, vasya, "Финиш"));
        await _site.SendAsync(new CastVote(polls[0].PollId, petya, "Дроп"));
        await _site.SendAsync(new CastVote(polls[0].PollId, vasya, "Реролл")); // the last one counts (D-513)
        await _site.SendAsync(new CastVote(polls[1].PollId, petya, "Финиш"));
        await _site.SendAsync(new ClosePoll(polls[1].PollId));

        await using var db = _site.NewDb();
        await AssertSchemaAsync(db, "Poll", "Id", "SeasonId", "Status", "ClosesAt");
        await AssertSchemaAsync(db, "PollOption", "PollId", "OptionId");
        Assert.Equal(2L, await ScalarAsync(db, "SELECT count(*) FROM Poll"));
        Assert.Equal(6L, await ScalarAsync(db, "SELECT count(*) FROM PollOption"));
        Assert.Equal(["Closed", "Open"], (await RowsAsync(db, "SELECT Status FROM Poll ORDER BY Status")).Select(r => (string)r[0]!));

        // One row per user and poll: the changed vote replaced the first
        var votes = (await RowsAsync(db, "SELECT OptionId FROM Vote ORDER BY OptionId")).Select(r => (string)r[0]!).ToList();
        Assert.Equal(["Дроп", "Реролл", "Финиш"], votes);
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_vote_row_changed_behind_the_queue_is_a_difference_of_its_poll()
    {
        await PollsOnAsync(_site);
        await _site.SendAsync(new StartPoll(Nomination()));
        var poll = Assert.Single((await StateAsync(_site)).Polls.Values);
        await _site.SendAsync(new CastVote(poll.PollId, _site.Users["vasya"], "Финиш"));
        await AssertIntactAsync(_site);

        await using (var db = _site.NewDb())
        {
            await AssertSchemaAsync(db, "Vote", "OptionId");
            Assert.Equal(1, await ExecuteAsync(db, "UPDATE Vote SET OptionId = 'Дроп'"));
        }

        var report = await IntegrityAsync(_site);
        Assert.Contains(report.Differences, d => d.StartsWith($"poll {poll.PollId}", StringComparison.Ordinal));
    }

    // ---- Global events ----

    [Fact]
    public async Task Global_events_are_projected_equal_to_the_log_and_a_changed_row_is_a_difference()
    {
        await GlobalEventsOnAsync(_site);
        await _site.SendAsync(new LaunchGlobalEvent(CoinRain.Id, DurationHours: 24, new GlobalEventGoal { Metric = GoalMetric.Runs, Target = 3 }));
        var ev = Assert.Single((await StateAsync(_site)).GlobalEvents.Values);

        await using (var db = _site.NewDb())
        {
            await AssertSchemaAsync(db, "GlobalEvent", "Id", "SeasonId", "Status", "EndsAt");
            Assert.Equal("Active", await ScalarAsync(db, "SELECT Status FROM GlobalEvent"));
            Assert.Equal(ev.EndsAt.UtcTicks, await ScalarAsync(db, "SELECT EndsAt FROM GlobalEvent"));
        }

        await AssertIntactAsync(_site);
        await using (var db = _site.NewDb())
        {
            await ExecuteAsync(db, "UPDATE GlobalEvent SET Status = 'Ended'");
        }

        Assert.Contains((await IntegrityAsync(_site)).Differences, d => d.StartsWith($"globalEvent {ev.GlobalEventId}", StringComparison.Ordinal));
    }

    // ---- Coop, invitations, the partner board ----

    [Fact]
    public async Task An_accepted_coop_invitation_projects_the_interaction_and_the_runs_partner()
    {
        await CoopOnAsync(_site);
        var (vasya, petya) = (_site.Players["vasya"], _site.Players["petya"]);
        var run = await PlayingAsync(_site, vasya);
        await _site.SendAsync(new InviteToCoop(vasya, petya));
        var invitation = Assert.Single((await StateAsync(_site)).Interactions.Values);
        await _site.SendAsync(new AnswerInvitation(petya, invitation.InteractionId, Accept: true));
        Assert.Equal(petya, (await StateAsync(_site)).Runs[run].PartnerId);

        await using (var db = _site.NewDb())
        {
            await AssertSchemaAsync(db, "Interaction", "Id", "SeasonId", "Kind", "FromPlayerId", "ToPlayerId", "Status", "ExpiresAt");
            await AssertSchemaAsync(db, "Run", "PartnerId");
            var row = (await RowsAsync(db, "SELECT Kind, Status, upper(FromPlayerId), upper(ToPlayerId) FROM Interaction")).Single();
            Assert.Equal(["Coop", "Accepted", vasya.ToString().ToUpperInvariant(), petya.ToString().ToUpperInvariant()], row.Select(x => (string)x!));
            Assert.Equal(petya.ToString().ToUpperInvariant(), await ScalarAsync(db, "SELECT upper(PartnerId) FROM Run WHERE PartnerId IS NOT NULL"));
        }

        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_changed_interaction_row_or_partner_column_is_a_difference()
    {
        await CoopOnAsync(_site);
        var (vasya, petya) = (_site.Players["vasya"], _site.Players["petya"]);
        var run = await PlayingAsync(_site, vasya);
        await _site.SendAsync(new InviteToCoop(vasya, petya));
        var invitation = Assert.Single((await StateAsync(_site)).Interactions.Values);
        await _site.SendAsync(new AnswerInvitation(petya, invitation.InteractionId, Accept: true));

        await using (var db = _site.NewDb())
        {
            await AssertSchemaAsync(db, "Interaction", "Status");
            await AssertSchemaAsync(db, "Run", "PartnerId");
            await ExecuteAsync(db, "UPDATE Interaction SET Status = 'Declined'");
            await ExecuteAsync(db, "UPDATE Run SET PartnerId = NULL");
        }

        var differences = (await IntegrityAsync(_site)).Differences;
        Assert.Contains(differences, d => d.StartsWith($"interaction {invitation.InteractionId}", StringComparison.Ordinal));
        Assert.Contains(differences, d => d.StartsWith($"run {run}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Requests_of_the_partner_board_are_projected_and_a_changed_row_is_a_difference()
    {
        await CoopOnAsync(_site, partnerBoard: true);
        var vasya = _site.Players["vasya"];
        var run = await PlayingAsync(_site, vasya);
        await _site.SendAsync(new PostPartnerRequest(vasya));
        var request = Assert.Single((await StateAsync(_site)).PartnerRequests.Values);

        await using (var db = _site.NewDb())
        {
            await AssertSchemaAsync(db, "PartnerRequest", "Id", "SeasonId", "RunId", "PlayerId", "Status", "ExpiresAt");
            var row = (await RowsAsync(db, "SELECT upper(RunId), Status FROM PartnerRequest")).Single();
            Assert.Equal([run.ToString().ToUpperInvariant(), "Open"], row.Select(x => (string)x!));
        }

        await AssertIntactAsync(_site);
        await using (var db = _site.NewDb())
        {
            await ExecuteAsync(db, "UPDATE PartnerRequest SET Status = 'Closed'");
        }

        Assert.Contains((await IntegrityAsync(_site)).Differences, d => d.StartsWith($"partnerRequest {request.RequestId}", StringComparison.Ordinal));
    }

    // ---- The weekly challenge ----

    [Fact]
    public async Task A_weekly_challenge_and_its_completions_are_projected_and_a_removed_completion_is_a_difference()
    {
        await RulesAsync(_site, Flags(f => f with { WeeklyChallenge = true }));
        var horrorWeek = new WeeklyChallengeDefinition
        {
            Id = "horror-week",
            Name = "Неделя хоррора",
            Description = "Пройди хоррор.",
            Condition = new ConditionSpec { Game = new GameFilterSpec { Tags = ["Horror"] } },
            Reward = new RewardSpec { Coins = 5 },
            Check = Engine.Content.ChallengeCheck.Auto,
        };
        await _site.SendAsync(new SetWeeklyChallenge(horrorWeek, _site.Clock.UtcNow));
        var vasya = _site.Players["vasya"];
        await PlayingAsync(_site, vasya);
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(5);
        await _site.SendAsync(new Engine.Runs.CompleteRun(vasya, Engine.Runs.Difficulty.Normal, 5m, "HLTB"));
        var challenge = Assert.Single((await StateAsync(_site)).WeeklyChallenges.Values);
        Assert.Single(challenge.Completions);

        await using (var db = _site.NewDb())
        {
            await AssertSchemaAsync(db, "WeeklyChallenge", "Id", "SeasonId", "StartsAt", "EndsAt");
            await AssertSchemaAsync(db, "WeeklyChallengeCompletion", "ChallengeId", "PlayerId", "RunId");
            Assert.Equal(1L, await ScalarAsync(db, "SELECT count(*) FROM WeeklyChallenge"));
            Assert.Equal(vasya.ToString().ToUpperInvariant(), await ScalarAsync(db, "SELECT upper(PlayerId) FROM WeeklyChallengeCompletion"));
        }

        await AssertIntactAsync(_site);
        await using (var db = _site.NewDb())
        {
            await ExecuteAsync(db, "DELETE FROM WeeklyChallengeCompletion");
        }

        Assert.Contains((await IntegrityAsync(_site)).Differences, d => d.StartsWith($"weeklyChallenge {challenge.ChallengeId}", StringComparison.Ordinal));
    }

    // ---- Suggestions ----

    [Fact]
    public async Task Suggestions_are_projected_and_a_changed_row_is_a_difference()
    {
        await RulesAsync(_site, Flags(f => f with { Suggestions = true }));
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new SubmitSuggestion(vasya, SuggestionKind.Event, "Ивент «Ночь хоррора»", null));
        var suggestion = Assert.Single((await StateAsync(_site)).Suggestions.Values);

        await using (var db = _site.NewDb())
        {
            await AssertSchemaAsync(db, "Suggestion", "Id", "SeasonId", "UserId", "Kind", "Status");
            Assert.Equal("Pending", await ScalarAsync(db, "SELECT Status FROM Suggestion"));
        }

        await _site.SendAsync(new ReviewSuggestion(suggestion.SuggestionId, Approve: false, "Уже есть похожий"));
        await using (var db = _site.NewDb())
        {
            Assert.Equal("Rejected", await ScalarAsync(db, "SELECT Status FROM Suggestion"));
        }

        await AssertIntactAsync(_site);
        await using (var db = _site.NewDb())
        {
            await ExecuteAsync(db, "UPDATE Suggestion SET Status = 'Approved'");
        }

        Assert.Contains((await IntegrityAsync(_site)).Differences, d => d.StartsWith($"suggestion {suggestion.SuggestionId}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_approved_game_suggestion_reaches_the_pool_by_the_pools_command_with_its_author()
    {
        // D-527, D-572: the season's log has no game-added; the queue adds the card to the pool with the author's account
        await RulesAsync(_site, Flags(f => f with { Suggestions = true }));
        var vasya = _site.Players["vasya"];
        var card = new GameCard("Signalis", ["Horror"], 9m, 2022, null, null, null, IsCoop: false);
        await _site.SendAsync(new SubmitSuggestion(vasya, SuggestionKind.Game, "Классный сурвайвал", card));
        var suggestion = Assert.Single((await StateAsync(_site)).Suggestions.Values);

        await _site.SendAsync(new ReviewSuggestion(suggestion.SuggestionId, Approve: true, null));

        await using var db = _site.NewDb();
        var game = await db.Games.AsNoTracking().SingleAsync(g => g.Title == "Signalis", Ct);
        Assert.Equal(9m, game.Hours);
        var added = await db.Events.AsNoTracking().SingleAsync(e => e.Type == "game-added" && e.Data.Contains("Signalis"), Ct);
        Assert.NotEqual(SiteFactory.SeasonId, added.SeasonId);
        Assert.Contains(_site.Users["vasya"].ToString(), added.Data, StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.Events.AnyAsync(e => e.SeasonId == SiteFactory.SeasonId && e.Type == "game-added", Ct));
    }

    [Fact]
    public async Task An_approved_game_already_in_the_pool_is_not_added_twice()
    {
        // D-572: the same check of duplicates as adding by hand; the approval itself stands
        await RulesAsync(_site, Flags(f => f with { Suggestions = true }));
        var vasya = _site.Players["vasya"];
        var card = new GameCard("Silent Hill", ["Horror"], 12m, null, null, null, null, IsCoop: false);
        await _site.SendAsync(new SubmitSuggestion(vasya, SuggestionKind.Game, "Классика", card));
        var suggestion = Assert.Single((await StateAsync(_site)).Suggestions.Values);
        int before;
        await using (var db = _site.NewDb())
        {
            before = await db.Games.CountAsync(Ct);
        }

        await _site.SendAsync(new ReviewSuggestion(suggestion.SuggestionId, Approve: true, null));

        await using var check = _site.NewDb();
        Assert.Equal(before, await check.Games.CountAsync(Ct));
        Assert.Equal(1, await check.Games.CountAsync(g => g.Title == "Silent Hill", Ct));
        Assert.Equal(SuggestionStatus.Approved, (await StateAsync(_site)).Suggestions[suggestion.SuggestionId].Status);
    }

    // ---- The run's conditions ----

    [Fact]
    public async Task A_runs_conditions_and_their_check_are_columns_equal_to_the_log()
    {
        // D-507, D-598: the conditions in the snapshot, their check in Run.ConditionsJson, whether met in Run.ConditionMet
        await RulesAsync(_site, r => r with
        {
            Features = r.Features with { Items = true, Events = true },
            Events = new EventRules { DefaultDeck = EventKind.Good, IrlProof = new IrlProofRules { MaxFiles = 5 } },
        });
        var vow = new ObjectDefinition
        {
            Id = "vow-hard",
            Kind = ObjectKind.Item,
            Name = "Обет",
            Description = "Следующее прохождение — на сложной.",
            Rarity = Rarity.Common,
            Price = 10,
            Window = UseWindow.Anytime,
            Stackable = true,
            Effect = new EffectSpec
            {
                Target = new TargetSpec { Selector = TargetSelector.Self },
                Actions = [new ModifyNextRollAction { RunCondition = new ConditionSpec { DifficultyAtLeast = Engine.Runs.Difficulty.Hard } }],
            },
        };
        await _site.SendAsync(new PublishContent(new ContentPack { Objects = [vow] }, "Контент"));
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new AdjustInventory(vasya, "vow-hard", null, "Подарок"));
        await _site.SendAsync(new UseItem(vasya, (await StateAsync(_site)).Players[vasya].Wallet.Inventory.Single(o => o.ObjectId == "vow-hard").InstanceId));
        var run = await PlayingAsync(_site, vasya);
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(5);
        await _site.SendAsync(new Engine.Runs.CompleteRun(vasya, Engine.Runs.Difficulty.Normal, 5m, "HLTB"));
        Assert.False((await StateAsync(_site)).Runs[run].Condition!.Met);

        await using (var db = _site.NewDb())
        {
            await AssertSchemaAsync(db, "Run", "ConditionsJson", "ConditionMet");
            var row = (await RowsAsync(db, "SELECT ConditionsJson, ConditionMet FROM Run")).Single();
            Assert.NotNull(row[0]);
            Assert.Equal(0L, row[1]);
        }

        await AssertIntactAsync(_site);
        await using (var db = _site.NewDb())
        {
            await ExecuteAsync(db, "UPDATE Run SET ConditionsJson = NULL, ConditionMet = NULL");
        }

        Assert.Contains((await IntegrityAsync(_site)).Differences, d => d.StartsWith($"run {run}", StringComparison.Ordinal));
    }

    // ---- The proof of an IRL effect ----

    [Fact]
    public async Task The_proof_of_an_irl_effect_is_projected_and_the_season_stays_intact()
    {
        // D-509, D-680: PendingManualEffect.IrlProof is stored with its effect, not only in the log
        var effect = await IrlEffectAsync();
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new Engine.Proofs.SubmitIrlProof(vasya, effect, [], "https://video.example/abc"));
        Assert.NotNull((await StateAsync(_site)).ManualEffects[effect].IrlProof);

        await AssertIntactAsync(_site);
        await using (var db = _site.NewDb())
        {
            await AssertSchemaAsync(db, "PendingManualEffect", "IrlProofJson");
            var proof = (string?)await ScalarAsync(db, "SELECT IrlProofJson FROM PendingManualEffect");
            Assert.Contains("https://video.example/abc", proof, StringComparison.Ordinal);
            await ExecuteAsync(db, "UPDATE PendingManualEffect SET IrlProofJson = NULL");
        }

        Assert.Contains((await IntegrityAsync(_site)).Differences, d => d.StartsWith($"manualEffect {effect}", StringComparison.Ordinal));
    }

    // ---- The integrity check knows the new parts of the state ----

    public static TheoryData<string> Kinds() => new("poll", "globalEvent", "interaction", "partnerRequest", "weeklyChallenge", "suggestion");

    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_integrity_check_names_an_entry_of_the_log_missing_from_the_stored_state(string kind)
    {
        var (fromLog, id) = WithOne(kind);
        var stored = Without(fromLog, kind);

        Assert.Contains($"{kind} {id}: in the log, not stored", SeasonIntegrity.Differences(fromLog, stored));
        Assert.Contains($"{kind} {id}: stored, not in the log", SeasonIntegrity.Differences(stored, fromLog));
        Assert.Empty(SeasonIntegrity.Differences(fromLog, fromLog));
    }

    [Fact]
    public void The_integrity_check_names_the_field_of_a_poll_that_differs()
    {
        var (fromLog, id) = WithOne("poll");
        var stored = fromLog with { Polls = fromLog.Polls.SetItem(id, fromLog.Polls[id] with { Status = PollStatus.Closed }) };

        Assert.Equal([$"poll {id}: status"], SeasonIntegrity.Differences(fromLog, stored));
    }

    // ---- The feed ----

    [Fact]
    public async Task In_the_feed_an_anonymous_vote_names_nobody_even_to_the_admin()
    {
        // D-515, D-726 through the real feed: the vote is sent by the voter's own account
        await PollsOnAsync(_site);
        await _site.SendAsync(new StartPoll(Nomination(anonymous: true)));
        var poll = Assert.Single((await StateAsync(_site)).Polls.Values);
        var user = _site.Users["vasya"];
        var outcome = await _site.Services.GetRequiredService<CommandBus>().SendAsync(
            new CommandEnvelope(Guid.NewGuid(), SiteFactory.SeasonId, new CastVote(poll.PollId, user, "Финиш"), AuthorId: user), Ct);
        Assert.True(outcome.IsAccepted, outcome.Rejection?.ToString());

        foreach (var viewer in new[] { "admin", "petya", "vasya" })
        {
            var client = await _site.SignedInAsync(viewer);
            using var feed = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}/feed", Ct));
            var vote = feed.RootElement.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("type").GetString() == "vote-cast");
            var text = vote.GetRawText();
            Assert.DoesNotContain(user.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_site.Players["vasya"].ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Финиш", text, StringComparison.Ordinal);
            Assert.DoesNotContain("vasya", text, StringComparison.Ordinal);
        }
    }

    // ---- Helpers ----

    /// <summary>
    /// Events on with an IRL event (<c>manual</c>, <c>proof: media</c>) as the only one of the good deck; Вася uses a «fortune»
    /// that draws it, which leaves a manual effect waiting for his proof. Returns its id.
    /// </summary>
    private async Task<Guid> IrlEffectAsync()
    {
        await RulesAsync(_site, r => r with
        {
            Features = r.Features with { Items = true, Events = true },
            Events = new EventRules { DefaultDeck = EventKind.Good, IrlProof = new IrlProofRules { MaxFiles = 5 } },
        });
        var squats = new ObjectDefinition
        {
            Id = "squats",
            Kind = ObjectKind.Event,
            Name = "20 приседаний",
            Description = "Сделай 20 приседаний на камеру.",
            Rarity = Rarity.Common,
            Manual = true,
            Proof = ContentProof.Media,
        };
        var fortune = new ObjectDefinition
        {
            Id = "fortune",
            Kind = ObjectKind.Item,
            Name = "Фортуна",
            Description = "Вытяни хороший ивент.",
            Rarity = Rarity.Common,
            Price = 10,
            Window = UseWindow.Anytime,
            Stackable = true,
            Effect = new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Self }, Actions = [new DrawEventAction { Deck = "good" }] },
        };
        await _site.SendAsync(new PublishContent(
            new ContentPack
            {
                Objects = [fortune, squats],
                Wheels = [new WheelDefinition { Id = "good", Name = "Хорошие", Deck = EventKind.Good, Entries = [new WheelEntrySpec { ObjectId = "squats", Weight = 1 }] }],
            },
            "Контент"));
        var vasya = _site.Players["vasya"];
        await _site.SendAsync(new AdjustInventory(vasya, "fortune", null, "Подарок"));
        await _site.SendAsync(new UseItem(vasya, (await StateAsync(_site)).Players[vasya].Wallet.Inventory.Single(o => o.ObjectId == "fortune").InstanceId));
        return Assert.Single((await StateAsync(_site)).ManualEffects.Values, e => e.ObjectId == "squats").EffectId;
    }

    private static readonly DateTimeOffset s_at = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A created season with one entry of <paramref name="kind"/>, built from the engine's public records.</summary>
    private static (SeasonState State, Guid Id) WithOne(string kind)
    {
        var (id, a, b) = (Guid.Parse("50000000-0000-0000-0000-000000000001"), Guid.NewGuid(), Guid.NewGuid());
        var state = SeasonState.Empty with { SeasonId = SiteFactory.SeasonId, Name = "Сезон", Ruleset = RulesetJson.Default(), RulesetVersion = 1 };
        state = kind switch
        {
            "poll" => state with
            {
                Polls = state.Polls.Add(id, new Poll(
                    id, PollSource.Admin, "Вопрос?", [new Engine.Polls.PollOption("да", "да")], PollVoters.Players, false, false, s_at, s_at.AddHours(24), PollStatus.Open, [])),
            },
            "globalEvent" => state with
            {
                GlobalEvents = state.GlobalEvents.Add(id, new GlobalEventState(id, CoinRain.Id, CoinRain, s_at, s_at.AddHours(24), GlobalEventStatus.Active, [], Reached: false)),
            },
            "interaction" => state with
            {
                Interactions = state.Interactions.Add(id, new Interaction(id, InteractionKind.Coop, a, b, Guid.NewGuid(), s_at, s_at.AddHours(24))),
            },
            "partnerRequest" => state with
            {
                PartnerRequests = state.PartnerRequests.Add(id, new PartnerRequest(id, a, Guid.NewGuid(), s_at, s_at.AddHours(48))),
            },
            "weeklyChallenge" => state with
            {
                WeeklyChallenges = state.WeeklyChallenges.Add(id, new WeeklyChallengeState(
                    id,
                    new WeeklyChallengeDefinition
                    {
                        Id = "hard-week",
                        Name = "Неделя харда",
                        Description = "Пройди на сложной.",
                        Condition = new ConditionSpec { DifficultyAtLeast = Engine.Runs.Difficulty.Hard },
                        Reward = new RewardSpec { Coins = 5 },
                        Check = Engine.Content.ChallengeCheck.Auto,
                    },
                    s_at,
                    s_at.AddDays(7),
                    5,
                    [new WeeklyChallengeCompletion(a, Guid.NewGuid(), 5)])),
            },
            "suggestion" => state with
            {
                Suggestions = state.Suggestions.Add(id, new Suggestion(id, a, b, SuggestionKind.Item, "Предмет «Зонтик»", null, s_at, SuggestionStatus.Pending, null)),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return (state, id);
    }

    private static SeasonState Without(SeasonState state, string kind) => kind switch
    {
        "poll" => state with { Polls = ImmutableSortedDictionary<Guid, Poll>.Empty },
        "globalEvent" => state with { GlobalEvents = ImmutableSortedDictionary<Guid, GlobalEventState>.Empty },
        "interaction" => state with { Interactions = ImmutableSortedDictionary<Guid, Interaction>.Empty },
        "partnerRequest" => state with { PartnerRequests = ImmutableSortedDictionary<Guid, PartnerRequest>.Empty },
        "weeklyChallenge" => state with { WeeklyChallenges = ImmutableSortedDictionary<Guid, WeeklyChallengeState>.Empty },
        "suggestion" => state with { Suggestions = ImmutableSortedDictionary<Guid, Suggestion>.Empty },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
