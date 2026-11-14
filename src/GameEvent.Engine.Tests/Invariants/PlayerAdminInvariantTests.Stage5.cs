using FsCheck.Xunit;
using GameEvent.Engine.Challenges;
using GameEvent.Engine.Content;
using GameEvent.Engine.Decks;
using GameEvent.Engine.Duels;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Effects;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Suggestions;
using GameEvent.Engine.Tests.Challenges;
using GameEvent.Engine.Tests.Duels;
using GameEvent.Engine.Tests.GlobalEvents;
using GameEvent.Engine.Tests.Polls;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Turns;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Invariants;

/// <summary>
/// Task 5.15: the random games of stage 5 — every mechanic of the stage at once in one season (EV35, EV38, EV47, EV49). Four
/// players roll, start, complete (an extreme difficulty draws a good event, D-504) and drop (the penalty draws a bad one),
/// step onto event cells drawing from the default deck, answer what an event asks, submit IRL proofs and the admin reviews
/// them and resolves manual effects; invite to coop, answer, withdraw and leave, post on the partner board and respond;
/// challenge to duels; the admin starts polls with and without an effect and players vote and change their votes, the timer
/// and the admin close them; the admin launches global events with goals of hours and of runs and ends them; sets an automatic
/// and a manual weekly challenge and marks it; players suggest and the admin reviews; the admin approves, rejects, corrects
/// hours, switches mechanics off and on (D-420), undoes commands and, late in a game, brings the deadline. The queue's
/// settlements for everyone run after each command (D-702).
/// After every command: a refusal changes nothing; 1 — the log replays to the state; 2 — points and coins are the sums of
/// their changes in the commands in force; 4 — one run per game, a partner without a run of his own (coop, D-520); 12 — the
/// chain limits; 13 — an accepted undo is the log without the command (<see cref="CheckUndo"/>); 16 — a settlement for
/// everyone, a close of a poll and a global event never change a begun step of another player; 18 — at most one vote per
/// user and poll; 19 — a mechanic that is off writes nothing (<see cref="CheckMechanics"/>); and the invariants new to the
/// stage — the coop shares add up to the throw and its coins (D-521), the progress of a goal is the sum of its contributions
/// and the last progress the log wrote (D-518), and a player holds at most one reward of a weekly challenge, never more than
/// its coins (D-526). 14 — the same seed and commands give the same log. Written by <c>test-author</c> from the SPEC.
/// </summary>
public partial class PlayerAdminInvariantTests
{
    private static readonly string[] s_stage5Players = ["Вася", "Петя", "Маша", "Коля"];

    private static readonly string[] s_stage5Games =
        ["Silent Hill", "Alan Wake", "Dead Space", "Outlast", "Soma", "Prey", "Doom", "Quake", "Limbo", "Inside", "Amnesia", "Portal 2", "It Takes Two"];

    private static readonly string[] s_globalObjects = ["coin-rain", "bonus-day", "gift-rain", "banner", "self-coins"];

    [Property(MaxTest = 120)]
    public void Invariants_hold_in_the_games_of_stage_5(int seed, byte[] script) =>
        PlayStage5(seed, script ?? [], CheckStage5Invariants);

    [Property(MaxTest = 30)]
    public void Same_seed_and_commands_give_the_same_log_in_the_games_of_stage_5(int seed, byte[] script)
    {
        var first = PlayStage5(seed, script ?? []);
        var second = PlayStage5(seed, script ?? []);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void Stage_5_games_reach_every_mechanic_and_undos_of_its_commands()
    {
        // The generator is worth its invariants only if it gets there: each mechanic of the stage, its undo and a refusal
        // of an undo for dependents (EV38)
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(string what) => counts[what] = counts.GetValueOrDefault(what) + 1;
        for (var seed = 0; seed < 100; seed++)
        {
            var x = (uint)seed + 13;
            var script = Enumerable.Range(0, 300).Select(_ => (byte)((x = (x * 1103515245) + 12345) >> 16)).ToArray();
            PlayStage5(seed, script, (s, command, before, _) =>
            {
                if (!s.Last.IsAccepted)
                {
                    if (command is UndoCommand && s.Last.Rejection!.Code == RejectionCodes.UndoDependents)
                    {
                        Count("undo refused");
                    }

                    return;
                }

                foreach (var e in s.Last.Events)
                {
                    Count(e switch
                    {
                        EventDrawn => "drawn",
                        EffectSuspended => "asked",
                        EffectResumed => "answered",
                        IrlProofSubmitted => "irl proof",
                        CoopJoined => "coop joined",
                        InteractionCreated { Interaction.Kind: InteractionKind.Coop } => "coop invited",
                        SeasonDeadlineSet => "deadline set",
                        CoopRewardSplit => "coop split",
                        CoopLeft => "coop left",
                        PartnerRequestPosted => "board",
                        DuelSettled => "duel settled",
                        PollStarted { Source: PollSource.Event } => "event poll",
                        PollClosed => "poll closed",
                        VoteChanged => "vote changed",
                        GlobalGoalReached => "goal reached",
                        GlobalProgressChanged => "progress",
                        ForAllSettled => "settled",
                        WeeklyChallengeCompleted => "challenge",
                        WeeklyChallengeRevoked => "challenge revoked",
                        SuggestionReviewed => "suggestion reviewed",
                        SeasonStatusChanged { To: SeasonStatus.Closing } => "deadline",
                        _ => "other",
                    });

                    // The vote of an event closes and its effect goes on with the voted game (D-512)
                    if (e is PollClosed pc && before.Polls.TryGetValue(pc.PollId, out var asking) && asking.Source == PollSource.Event)
                    {
                        Count("event poll closed");
                    }
                }

                if (command is UndoCommand undone && s.Commands[s.History.ToList().FindIndex(c => c.CommandId == undone.TargetCommandId)] is { } target_)
                {
                    Count($"undone {target_.GetType().Name}");
                }

                if (command is UndoCommand undo)
                {
                    // An undo of a command of stage 5 — one that wrote an event of a stage 5 mechanic
                    var target = s.History.Single(c => c.CommandId == undo.TargetCommandId);
                    if (target.Events.Any(e => MechanicOf(e) is not null and not "challenges" and not "graphMap"))
                    {
                        Count("stage 5 undone");
                    }
                }
            });
        }

        string[] expected =
        [
            "coop invited", "coop joined", "deadline set", "drawn", "asked", "answered", "irl proof", "coop split", "coop left", "board", "duel settled", "event poll", "event poll closed", "poll closed", "vote changed",
            "goal reached", "progress", "settled", "challenge", "challenge revoked", "suggestion reviewed", "deadline", "stage 5 undone",
            "undo refused",
        ];
        // EV38: every command of stage 5 is undone as a whole somewhere in the games
        var commands = s_stage5Commands.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray();
        expected = [.. expected, .. commands.Select(c => $"undone {c}")];
        var missing = expected.Where(what => counts.GetValueOrDefault(what) == 0).ToList();
        Assert.True(missing.Count == 0, $"The stage 5 games never reached: {string.Join(", ", missing)}. Reached: {string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}"))}.");
    }

    /// <summary>Every flag of stage 5 on, with the numbers of their decisions; suggestions three a day.</summary>
    private static Ruleset Stage5Rules()
    {
        var rules = TestRuleset.Create()
            .WithGlobalEvents(maxActive: 2)
            .WithPolls()
            .WithPartnerBoard()
            .WithDuels(d => d with { MaxOpenPerPlayer = 2, MaxWinsPerSeason = 3 })
            .WithWeeklyChallenge();
        return rules with
        {
            Features = rules.Features with { Suggestions = true },
            Suggestions = new SuggestionRules { PerDay = 3 },
            Events = rules.Events! with { AnswerTimeoutHours = 24 },
        };
    }

    /// <summary>
    /// The season of the stage 5 games: a line of 24 cells with five event cells drawing from the default deck (risky), the
    /// horror pool with every other game coop, the content of global events (<see cref="GlobalSetup.BaseObjects"/>: «Рискнёшь?»,
    /// «Монетка или шаг», «Караоке» with its IRL proof, «Хоррор-неделя», «Попутный ветер») in three decks, the good one with
    /// «Выберите игру лидеру» too — a vote of the players on the leader's next game (<c>requestChoice.vote</c>, then
    /// <c>modifyNextRoll.filter.games</c>, D-512).
    /// </summary>
    private static Scenario NewStage5Season(int seed)
    {
        var cells = Enumerable.Range(1, 24).Select(i => i % 4 == 3 ? $"e{i}" : $"c{i}").ToArray();
        var map = MapBuilder.New();
        foreach (var cell in cells.Where(c => c.StartsWith('e')))
        {
            map.EventCell(cell);
        }

        map.Path(["start", .. cells, "finish"]);
        var s = Scenario.New(Stage5Rules(), seed).WithMap(map.Build()).WithCategory("Horror");
        foreach (var title in s_stage5Games)
        {
            s.WithGame(title, 3 + (3 * (title.Length % 3)), "Horror");
        }

        // Half the pool is coop, so invitations meet runs they can join
        foreach (var title in s_stage5Games.Where((_, i) => i % 2 == 0))
        {
            s.MarkCoop(title);
        }

        s.WithPlayers(s_stage5Players);
        return s.WithContent(GlobalSetup.Pack(
            [Polls.EventPollTests.LeaderPick],
            EventScenario.Deck(EventKind.Risky, "risky", "gamble", "coin-or-step"),
            EventScenario.Deck(EventKind.Good, "good", "tailwind", "karaoke", "leader-pick"),
            EventScenario.Deck(EventKind.Bad, "bad", "horror-week", "karaoke")));
    }

    private static Scenario PlayStage5(int seed, byte[] script, Action<Scenario, ICommand, SeasonState, int>? afterEach = null)
    {
        var s = NewStage5Season(seed);
        var setup = s.History.Count;
        return s.Explained(x =>
        {
            for (var i = 0; i < script.Length; i++)
            {
                Stage5Step(x, Stage5CommandFor(x, script[i], setup, late: i >= 200), afterEach);

                // The queue settles what a command queued before the next command (D-702)
                for (var k = 0; k < 10 && x.Last.IsAccepted && ForAllSettlements.Next(x.State) is { } id; k++)
                {
                    Stage5Step(x, new SettleForAll(id), afterEach);
                }
            }
        });
    }

    private static void Stage5Step(Scenario s, ICommand command, Action<Scenario, ICommand, SeasonState, int>? afterEach)
    {
        var before = s.State;
        var logLength = s.Log.Count;
        s.Act(command);
        afterEach?.Invoke(s, command, before, logLength);
    }

    private static ICommand Stage5CommandFor(Scenario s, byte b, int setup, bool late)
    {
        var state = s.State;
        var arg = b / 24;
        var name = s_stage5Players[arg % s_stage5Players.Length];
        var player = state.Players[s.PlayerId(name)];
        var other = s.PlayerId(s_stage5Players[(arg + 1 + (b % 3)) % s_stage5Players.Length]);
        var completed = state.Runs.Values.Where(r => r.Status == RunStatus.Completed).OrderBy(r => r.RunId).ToList();
        var pendingTo = state.Interactions.Values.FirstOrDefault(i => i.Status == InteractionStatus.Pending && i.ToPlayerId == player.PlayerId);
        var pendingFrom = state.Interactions.Values.FirstOrDefault(i => i.Status == InteractionStatus.Pending && i.FromPlayerId == player.PlayerId);
        var polls = state.Polls.Values.Where(p => p.Status == PollStatus.Open).ToList();
        // An idle player invited to coop takes it before anything else he would do, mostly
        if (player.Phase == TurnPhase.Idle && pendingTo is { Kind: InteractionKind.Coop } && b % 24 is < 17 && arg % 4 != 0)
        {
            return new AnswerInvitation(player.PlayerId, pendingTo.InteractionId, Accept: true);
        }

        switch (b % 24)
        {
            case 0 or 1 or 2 or 3:
                if (player.Choice is { } choice && arg % 2 == 0)
                {
                    return Answer(player, choice, arg);
                }

                // An idle player answers a request on the board now and then; a partner leaves (his turn is the roller's to complete)
                if (player.Phase == TurnPhase.Idle && arg % 3 == 1
                    && state.PartnerRequests.Values.FirstOrDefault(r => r.Status == PartnerRequestStatus.Open && r.PlayerId != player.PlayerId) is { } request)
                {
                    return new RespondToPartnerRequest(player.PlayerId, request.RequestId);
                }

                if (state.Runs.Values.Any(r => r.Status == RunStatus.Playing && r.PartnerId == player.PlayerId) && b % 2 == 0)
                {
                    return new LeaveCoop(player.PlayerId);
                }

                return player.Phase switch
                {
                    TurnPhase.Idle => new RollGame(player.PlayerId),
                    TurnPhase.Rolling => new StartRun(player.PlayerId),
                    _ => (arg % 5) switch
                    {
                        0 => new DropRun(player.PlayerId),
                        1 => new CompleteRun(player.PlayerId, Difficulty.Extreme),
                        2 => new CompleteRun(player.PlayerId, Difficulty.Hard),
                        _ => new CompleteRun(player.PlayerId, Difficulty.Normal),
                    },
                };
            case 4 when player.Choice is { } asked:
                return Answer(player, asked, arg);
            case 4 or 5 when pendingTo is null && player.Phase != TurnPhase.Playing:
                return player.Phase == TurnPhase.Idle ? new RollGame(player.PlayerId) : new StartRun(player.PlayerId);
            case 4 or 5:
                return pendingTo is not null
                    ? new AnswerInvitation(player.PlayerId, pendingTo.InteractionId, Accept: arg % 4 != 0)
                    : new InviteToCoop(player.PlayerId, state.Players.Values.FirstOrDefault(p => p.PlayerId != player.PlayerId && p.Phase == TurnPhase.Idle)?.PlayerId ?? other);
            case 6:
                var partnering = state.Runs.Values.Any(r => r.Status == RunStatus.Playing && r.PartnerId == player.PlayerId);
                return (partnering, pendingFrom) switch
                {
                    (true, _) when arg % 3 != 0 => new LeaveCoop(player.PlayerId),
                    (_, { } sent) when arg % 3 != 0 => new WithdrawInvitation(player.PlayerId, sent.InteractionId),
                    _ => new ChallengeToDuel(player.PlayerId, other),
                };
            case 7:
                // The board: a player with a request open closes it now and then, an idle one responds, one playing posts
                var mine = state.PartnerRequests.Values.FirstOrDefault(r => r.Status == PartnerRequestStatus.Open && r.PlayerId == player.PlayerId);
                var open = state.PartnerRequests.Values.FirstOrDefault(r => r.Status == PartnerRequestStatus.Open && r.PlayerId != player.PlayerId);
                return (player.Phase, open, mine) switch
                {
                    (_, _, { } own) when arg % 2 == 0 => new ClosePartnerRequest(player.PlayerId, own.RequestId),
                    (TurnPhase.Idle, { } posted, _) => new RespondToPartnerRequest(player.PlayerId, posted.RequestId),
                    (TurnPhase.Idle, _, _) => new RollGame(player.PlayerId),
                    (TurnPhase.Rolling, _, _) => new StartRun(player.PlayerId),
                    _ => new PostPartnerRequest(player.PlayerId),
                };
            case 8:
                return new StartPoll((arg % 3) switch
                {
                    0 => PollSetup.ContentPoll(),
                    1 => PollSetup.EffectPoll(GlobalSetup.OnAll(GlobalSetup.Coins(1))),
                    _ => PollSetup.ListPoll(PollVoters.Players, anonymous: true, options: ["Да", "Нет"]),
                });
            case 9 or 10 when polls.Count > 0:
                var poll = polls[arg % polls.Count];
                return new CastVote(poll.PollId, player.UserId, poll.Options[(arg + b) % poll.Options.Count].Id);
            case 11 when polls.Count > 0:
                return new ClosePoll(polls[arg % polls.Count].PollId);
            case 12:
                var running = state.GlobalEvents.Values.FirstOrDefault(g => g.Status != GlobalEventStatus.Ended);
                return running is not null && arg % 3 == 0
                    ? new EndGlobalEvent(running.GlobalEventId)
                    : new LaunchGlobalEvent(
                        s_globalObjects[arg % s_globalObjects.Length],
                        12 + (arg % 60),
                        (arg % 3) switch
                        {
                            0 => GlobalSetup.HoursGoal(6 + (arg % 12), GlobalSetup.OnAll(GlobalSetup.Coins(1))),
                            1 => GlobalSetup.RunsGoal(1 + (arg % 3), GlobalSetup.OnAll(GlobalSetup.Coins(1), GlobalSetup.Points(1))),
                            _ => null,
                        });
            case 13:
                var challenge = state.WeeklyChallenges.Values.LastOrDefault(c => c.Definition.Check == ChallengeCheck.Manual);
                var horror = completed.Where(r => r.PlayerId == player.PlayerId).LastOrDefault();
                return challenge is not null && horror is not null && arg % 2 == 0
                    ? new MarkWeeklyChallenge(challenge.ChallengeId, player.PlayerId, horror.RunId)
                    : new SetWeeklyChallenge(arg % 2 == 0 ? WeeklyChallengeScenario.Manual() : WeeklyChallengeScenario.HorrorWeek(), s.Clock.UtcNow);
            case 14:
                var waiting = state.Suggestions.Values.FirstOrDefault(x => x.Status == SuggestionStatus.Pending);
                return waiting is not null && arg % 2 == 0
                    ? new ReviewSuggestion(waiting.SuggestionId, Approve: arg % 4 == 0, "Спасибо")
                    : new SubmitSuggestion(player.PlayerId, SuggestionKind.Event, $"Ивент {b}", null);
            case 15:
                var effect = state.ManualEffects.Values.FirstOrDefault(e => e.PlayerId == player.PlayerId) ?? state.ManualEffects.Values.FirstOrDefault();
                return (effect, effect?.IrlProof?.Status) switch
                {
                    (null, _) => new FireTimers(),
                    ({ ObjectId: "karaoke" }, null or IrlProofStatus.Rejected) when effect.PlayerId == player.PlayerId =>
                        new SubmitIrlProof(player.PlayerId, effect.EffectId, [], "https://video.example/karaoke"),
                    (_, IrlProofStatus.Pending) => new ReviewIrlProof(effect.EffectId, Approved: arg % 3 != 0, "Видео"),
                    _ => new ResolveManualEffect(effect.EffectId, arg % 2 == 0 ? ManualEffectOutcome.Applied : ManualEffectOutcome.NotApplicable, "Сделано", null),
                };
            case 16 when completed.Count > 0:
                var run = completed[arg % completed.Count];
                return (arg % 4) switch
                {
                    0 => new RejectProof(run.RunId, "Нет титров"),
                    1 => new CorrectRunHours(run.RunId, run.Hours == 9 ? 3 : 9, "Правка часов"),
                    _ => new ApproveProof(run.RunId),
                };
            case 17 or 18:
                s.Advance(TimeSpan.FromHours(1 + (arg % 12) + (b % 2 == 0 ? 20 : 0)));
                return new FireTimers();
            case 19 when s.History.Count > setup && LatestStage5Command(s, setup) is { } latest:
                // The latest command of stage 5 still in force, so each of them meets its undo, often with dependents after it
                return new UndoCommand(latest, "ошибка админа");
            case 19 or 20 when s.History.Count > setup:
                // The game's own commands only: the setup (players, content) stays (D-566); the last most often, else an
                // earlier one, so undos meet their dependents
                var played = s.History.Count - setup;
                return new UndoCommand(
                    s.History[b % 24 == 19 ? setup + (arg % played) : s.History.Count - 1 - Math.Min(arg % 3, played - 1)].CommandId,
                    "ошибка админа");
            case 21 when s.Ruleset.Features != Stage5Rules().Features:
                // A mechanic switched off comes back on at the next change, so the games see both
                return new ChangeRuleset(s.Ruleset with { Features = Stage5Rules().Features with { MapMode = s.Ruleset.Features.MapMode } });
            case 21:
                // D-420: a mechanic switched off mid-season freezes; one in play refuses the switch
                var features = s.Ruleset.Features;
                return new ChangeRuleset(s.Ruleset with
                {
                    Features = (arg % 7) switch
                    {
                        0 => features with { Coop = !features.Coop, PartnerBoard = !features.Coop && features.PartnerBoard },
                        1 => features with { Polls = !features.Polls },
                        2 => features with { GlobalEvents = !features.GlobalEvents },
                        3 => features with { Duels = !features.Duels },
                        4 => features with { WeeklyChallenge = !features.WeeklyChallenge },
                        5 => features with { Suggestions = !features.Suggestions },
                        _ => features with { Events = !features.Events, GlobalEvents = !features.Events && features.GlobalEvents },
                    },
                });
            case 22 when late && state.Status == SeasonStatus.Active && arg % 4 == 0:
                // Late in a game the deadline comes: no more rolls, proofs and reviews go on (K-7)
                return state.Deadline is { } deadline && deadline <= s.Clock.UtcNow ? new ReachDeadline() : new SetSeasonDeadline(s.Clock.UtcNow.AddHours(1));
            case 22:
                return new SetPlayerInactive(player.PlayerId, !player.IsInactive);
            default:
                return new AdjustPlayer(player.PlayerId, "Очки для теста", PointsDelta: arg % 2 == 0 ? 3 : -2);
        }
    }

    private static readonly HashSet<Type> s_stage5Commands =
    [
        typeof(StartPoll), typeof(CastVote), typeof(ClosePoll), typeof(InviteToCoop), typeof(AnswerInvitation), typeof(WithdrawInvitation),
        typeof(LeaveCoop), typeof(PostPartnerRequest), typeof(RespondToPartnerRequest), typeof(ClosePartnerRequest), typeof(ChallengeToDuel),
        typeof(SetWeeklyChallenge), typeof(MarkWeeklyChallenge), typeof(SubmitSuggestion), typeof(ReviewSuggestion), typeof(LaunchGlobalEvent),
        typeof(EndGlobalEvent), typeof(SettleForAll), typeof(SubmitIrlProof), typeof(ReviewIrlProof), typeof(MakeChoice), typeof(ResolveManualEffect),
    ];

    // Rare in the games: preferred by the undos while among the last commands
    private static readonly HashSet<Type> s_rareStage5Commands =
        [typeof(LeaveCoop), typeof(WithdrawInvitation), typeof(ClosePartnerRequest), typeof(RespondToPartnerRequest), typeof(MakeChoice)];

    /// <summary>
    /// The id of the latest command of stage 5 of the game (after <paramref name="setup"/>) not undone, a rare one among the
    /// last six first; null when there is none.
    /// </summary>
    private static Guid? LatestStage5Command(Scenario s, int setup)
    {
        var undone = s.UndoneCommands();
        for (var i = s.History.Count - 1; i >= Math.Max(setup, s.History.Count - 6); i--)
        {
            if (s.Commands[i] is { } command && s_rareStage5Commands.Contains(command.GetType()) && !undone.Contains(s.History[i].CommandId))
            {
                return s.History[i].CommandId;
            }
        }

        for (var i = s.History.Count - 1; i >= setup; i--)
        {
            if (s.Commands[i] is { } command && s_stage5Commands.Contains(command.GetType()) && !undone.Contains(s.History[i].CommandId))
            {
                return s.History[i].CommandId;
            }
        }

        return null;
    }

    /// <summary>A listed option of the question, or one past them — an answer off the list.</summary>
    private static MakeChoice Answer(SeasonPlayer player, PendingChoice choice, int arg)
    {
        var index = arg % (choice.Options.Count + 1);
        return new MakeChoice(player.PlayerId, choice.ChoiceId, index < choice.Options.Count ? choice.Options[index].Id : "нет такого");
    }

    private static void CheckStage5Invariants(Scenario s, ICommand command, SeasonState before, int logLengthBefore)
    {
        CheckMechanics(s, command, before);
        CheckStage5Undo(s, command, before);
        if (!s.Last.IsAccepted)
        {
            Assert.Equal(before, s.State);
            Assert.Equal(logLengthBefore, s.Log.Count);
            return;
        }

        var state = s.State;
        var log = s.EffectiveLog;

        // 1: the state rebuilt from the log, through its stored form, is the state
        Assert.Equal(state, SeasonEngine.Replay(s.StoredLog));

        // 2: points and coins are the sums of their changes in the commands in force
        foreach (var p in state.Players.Values)
        {
            Assert.Equal(log.OfType<PointsChanged>().Where(e => e.PlayerId == p.PlayerId).Sum(e => e.Delta), p.Points);
            Assert.Equal(log.OfType<CoinsChanged>().Where(e => e.PlayerId == p.PlayerId).Sum(e => e.Delta), p.Coins);
        }

        // 4: one run per game at a time; a partner plays only the run he joined and has none of his own (D-520)
        var playing = state.Runs.Values.Where(r => r.Status == RunStatus.Playing).ToList();
        Assert.Equal(playing.Count, playing.Select(r => r.GameId).Distinct().Count());
        foreach (var p in state.Players.Values)
        {
            var mine = playing.Where(r => r.PlayerId == p.PlayerId || r.PartnerId == p.PlayerId).ToList();
            Assert.True(mine.Count <= 1, $"{s.PlayerName(p.PlayerId)} plays {mine.Count} runs.");
            Assert.Equal(mine.SingleOrDefault()?.RunId, p.ActiveRunId);
        }

        Assert.All(playing.Where(r => r.PartnerId is not null), r => Assert.NotEqual(r.PlayerId, r.PartnerId));

        // 12: within the chain limits — 50 events and the cut
        Assert.True(s.Last.Events.Count <= Limits.MaxEventsPerCommand + 1, $"{s.Last.Events.Count} events in one command.");
        var cut = s.Last.Events.OfType<EffectChainCut>().SingleOrDefault();
        Assert.True(cut is null || cut.Events <= Limits.MaxEventsPerCommand);

        // 16: what acts on everyone, or on another player, never changes a step he has begun
        var owner = command switch
        {
            ClosePoll close when before.Polls.TryGetValue(close.PollId, out var closed) => closed.PlayerId,
            _ => null,
        };
        if (command is SettleForAll or ClosePoll or LaunchGlobalEvent or EndGlobalEvent)
        {
            foreach (var p in before.Players.Values.Where(p => p.PlayerId != owner))
            {
                var after = state.Players[p.PlayerId];
                Assert.Equal((p.Offer, p.Wallet.CurrentRoll), (after.Offer, after.Wallet.CurrentRoll));
                if (p.Choice is not null)
                {
                    Assert.Equal(p.Choice, after.Choice);
                }

                if (p.ActiveRunId is { } active)
                {
                    Assert.Equal(before.Runs[active], state.Runs[active]);
                }
            }
        }

        // 18: at most one vote in force per user and poll
        foreach (var poll in state.Polls.Values)
        {
            Assert.Equal(poll.Votes.Count, poll.Votes.Select(v => v.UserId).Distinct().Count());
        }

        // New: the coop shares add up to the throw and its coins (D-521, EV46) — at the split and after every correction
        foreach (var split in s.LastEvents<CoopRewardSplit>())
        {
            Assert.Equal(split.Total, split.RollerPoints + split.PartnerPoints);
            Assert.Equal(split.Coins, split.RollerCoins + split.PartnerCoins);
        }

        foreach (var run in state.Runs.Values.Where(r => r.Status == RunStatus.Completed && r.Coop?.Shares is not null))
        {
            Assert.Equal(CompletionRoll.Total(run), RunShares.Players(run).Sum(id => RunShares.Points(run, id)));
            var coins = run.Snapshot.Coins is { } reward ? RunLifecycle.CompletionCoins(reward, run.Snapshot.DiceCount, run.Hours!.Value) : 0;
            Assert.Equal(coins, RunShares.Players(run).Sum(id => RunShares.Coins(run, id, coins)));
        }

        // New: the progress of a goal is the sum of its contributions — one per run still completed — and of the changes in
        // force; a change writes the progress as it is then (D-750: «разница и итог»). An undo of an earlier change leaves the
        // totals written after it as they were (D-800): readers take the progress from the contributions, not the last total.
        foreach (var global in state.GlobalEvents.Values)
        {
            Assert.Equal(global.Contributions.Count, global.Contributions.Select(c => c.RunId).Distinct().Count());
            Assert.All(global.Contributions, c => Assert.Equal(RunStatus.Completed, state.Runs[c.RunId].Status));
            var changes = log.OfType<GlobalProgressChanged>().Where(p => p.GlobalEventId == global.GlobalEventId).ToList();
            Assert.Equal(changes.Sum(c => c.Delta), global.Progress);
            if (s.LastEvents<GlobalProgressChanged>().LastOrDefault(p => p.GlobalEventId == global.GlobalEventId) is { } written)
            {
                Assert.Equal(written.Total, global.Progress);
            }

            Assert.InRange(log.OfType<GlobalGoalReached>().Count(r => r.GlobalEventId == global.GlobalEventId), 0, 1);
        }

        // New: at most one reward of a weekly challenge per player, never more than its coins (D-526)
        foreach (var challenge in state.WeeklyChallenges.Values)
        {
            Assert.Equal(challenge.Completions.Count, challenge.Completions.Select(c => c.PlayerId).Distinct().Count());
            Assert.All(challenge.Completions, c => Assert.InRange(c.Coins, 0, challenge.RewardCoins));
        }

        foreach (var p in state.Players.Values)
        {
            var paid = log.OfType<CoinsChanged>()
                .Where(c => c.PlayerId == p.PlayerId && c.Reason is CoinsReason.WeeklyChallenge or CoinsReason.WeeklyChallengeRevoked)
                .Sum(c => c.Delta);
            Assert.InRange(paid, 0, state.WeeklyChallenges.Values.Where(c => c.Completions.Any(x => x.PlayerId == p.PlayerId)).Sum(c => c.RewardCoins)
                + (Engine.Finish.Finishes.IsFrozen(p) ? state.WeeklyChallenges.Values.Sum(c => c.RewardCoins) : 0));
        }

        // Duels (EV41): two players have at most one open duel — open is what still waits or runs by the clock (D-732: a
        // challenge past its time is no longer answered, the timer only writes it off); a won duel names one of its two
        var now = s.Clock.UtcNow;
        var openDuels = state.Duels.Values.Where(d => d.Status == DuelStatus.Active && now < d.ExpiresAt).Select(d => Pair(d.ChallengerId, d.OpponentId))
            .Concat(state.Interactions.Values
                .Where(i => i.Kind == InteractionKind.Duel && i.Status == InteractionStatus.Pending && now < i.ExpiresAt)
                .Select(i => Pair(i.FromPlayerId, i.ToPlayerId)))
            .ToList();
        Assert.Equal(openDuels.Count, openDuels.Distinct().Count());
        Assert.All(state.Duels.Values.Where(d => d.WinnerId is not null), d => Assert.Contains(d.WinnerId!.Value, new[] { d.ChallengerId, d.OpponentId }));
    }

    /// <summary>
    /// 13 for stage 5: as <see cref="CheckUndo"/>, except that the undo of a batch of a settlement for everyone halts the
    /// settlement, so the queue does not give the batch again (D-700, «Undoing_the_last_batch_puts_its_players_back_and_halts_the_settlement»):
    /// the season is the log without the commands undone, but for the halts those undos wrote.
    /// </summary>
    private static void CheckStage5Undo(Scenario s, ICommand command, SeasonState before)
    {
        if (command is not UndoCommand undo || !s.Last.IsAccepted)
        {
            CheckUndo(s, command, before);
            return;
        }

        var history = s.History.ToList();
        var undone = Assert.IsType<CommandUndone>(Assert.Single(s.Last.Events));
        Assert.Equal((undo.TargetCommandId, undo.Comment.Trim()), (undone.CommandId, undone.Comment));
        var earlier = s.UndoneCommands();
        earlier.Remove(undo.TargetCommandId);
        var at = history.FindIndex(c => c.CommandId == undo.TargetCommandId);
        Assert.True(Scenario.IsEffective(history[at], earlier), "An undo of a command that did not count.");
        Assert.True(before.Status is SeasonStatus.Draft or SeasonStatus.Active or SeasonStatus.Closing, $"Undone while {before.Status}.");
        if (s.Commands[at] is SettleForAll settle)
        {
            Assert.True(s.State.ForAll[settle.SettlementId].Halted, "The undo of a batch did not halt its settlement.");
        }

        static SeasonState Unhalted(SeasonState state) =>
            Comparable(state) with { ForAll = state.ForAll.SetItems(state.ForAll.Select(f => KeyValuePair.Create(f.Key, f.Value with { Halted = false }))) };
        Assert.Equal(Unhalted(SeasonEngine.Replay(s.EffectiveLog)), Unhalted(s.State));
    }

    private static (Guid, Guid) Pair(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);
}
