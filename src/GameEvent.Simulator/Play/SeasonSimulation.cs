using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;
using GameEvent.Simulator.Setup;

namespace GameEvent.Simulator.Play;

/// <summary>
/// One season played by bots through the engine's public contract (D-350): every change is a command of
/// <see cref="SeasonEngine.Execute"/>, as on the site; the bots only decide which command to send and when. Time is a
/// queue of wakes in simulated time — bots' sessions, the admin bot's checks, the deadline — taken in order, so bots
/// interleave as real players would. A seed gives the same season.
/// </summary>
public sealed class SeasonSimulation
{
    /// <summary>A bot acts at most this many times per wake: a refused command never makes it loop (D-355).</summary>
    public const int MaxActionsPerWake = 40;

    private const double Epsilon = 1e-9;
    private const string ProofLink = "https://example.com/proof";

    private static readonly Guid s_seasonId = SimIds.Make(0x5ea50000, 1);
    private static readonly TechRerollReason[] s_techReasons =
        [TechRerollReason.WeakPc, TechRerollReason.PaidUnavailable, TechRerollReason.DoesNotLaunch, TechRerollReason.EmulatorTooSlow];

    private static readonly Difficulty[] s_difficulties = [Difficulty.Easy, Difficulty.Normal, Difficulty.Hard, Difficulty.Extreme];

    private readonly SimulationInputs _inputs;
    private readonly int _index;
    private readonly ulong _seed;
    private readonly int _days;
    private readonly SimClock _clock;
    private readonly SimRandom _bots;
    private readonly EngineContext _context;
    private readonly DateTimeOffset _start;
    private readonly DateTimeOffset _deadline;
    private readonly PriorityQueue<Wake, (long Ticks, int Order, long Seq)> _queue = new();
    private readonly List<Bot> _players = [];
    private readonly Dictionary<Guid, Bot> _byPlayer = [];
    private readonly Dictionary<Guid, RunRecord> _runs = [];
    private readonly List<RunRecord> _runOrder = [];
    private readonly List<BranchOutcome> _branches = [];
    private readonly SortedDictionary<string, int> _rejections = new(StringComparer.Ordinal);
    private readonly double[] _difficultyWeights;
    private IReadOnlyDictionary<string, int>? _cellsToFinish;
    private SeasonState _state = SeasonState.Empty;
    private long _seq;
    private int _commands;
    private int _events;
    private int _misses;
    private int _guardTrips;

    public SeasonSimulation(SimulationInputs inputs, int days, int index, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        _inputs = inputs;
        _index = index;
        _seed = seed;
        _days = days;
        _start = inputs.Settings.Start;
        _deadline = _start.AddDays(days);
        _clock = new SimClock(_start.AddHours(-1));
        _bots = new SimRandom(SimRandom.Mix(seed, 2));
        _context = new EngineContext(_clock, new SimRandom(SimRandom.Mix(seed, 1)), new SimIds(0x51300000), inputs.Pool);
        var weights = inputs.Settings.Behaviour.Difficulty;
        _difficultyWeights = [.. s_difficulties.Select(d => weights.TryGetValue(d.ToString().ToLowerInvariant(), out var w) ? w : 0)];
    }

    private enum WakeKind
    {
        Deadline,
        FinalReview,
        AdminCheck,
        Bot,
    }

    /// <summary>Plays the season to its result.</summary>
    public SeasonOutcome Run()
    {
        SetUp();
        while (_queue.TryDequeue(out var wake, out var at))
        {
            _clock.MoveTo(new DateTimeOffset(at.Ticks, TimeSpan.Zero));
            switch (wake.Kind)
            {
                case WakeKind.Deadline:
                    Execute(new ReachDeadline(), bot: null);
                    break;
                case WakeKind.AdminCheck:
                    AdminCheck(final: false);
                    break;
                case WakeKind.FinalReview:
                    AdminCheck(final: true);
                    Require(Execute(new ChangeSeasonStatus(SeasonStatus.Finished), bot: null), "finish the season");
                    return Collect();
                case WakeKind.Bot:
                    BotWake(_players[wake.Bot], wake.Session);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown wake {wake.Kind}.");
            }
        }

        throw new InvalidOperationException("The season ran out of wakes before its final review.");
    }

    private void SetUp()
    {
        var map = _inputs.Map;
        Require(Execute(new CreateSeason(s_seasonId, "Симуляция", _inputs.Ruleset, _deadline, map), bot: null), "create the season");
        _cellsToFinish = Leaderboard.CellsToFinish(_state.Map);

        var sessions = new SimRandom(SimRandom.Mix(_seed, 3));
        var behaviour = _inputs.Settings.Behaviour;
        foreach (var group in _inputs.Settings.Players)
        {
            var profile = _inputs.Settings.Profiles[group.Profile];
            var droppers = (int)Math.Round(group.Count * behaviour.DropperShare, MidpointRounding.AwayFromZero);
            for (var k = 0; k < group.Count; k++)
            {
                // Policies rotate with the run: over many runs every profile plays every policy evenly
                var index = _players.Count;
                var policy = behaviour.BranchPolicies[(k + _index) % behaviour.BranchPolicies.Count];
                var bot = new Bot(
                    index,
                    SimIds.Make(0xb0700000, index + 1),
                    group.Profile,
                    k < droppers,
                    policy,
                    Sessions(profile, sessions));
                _players.Add(bot);
                _byPlayer[bot.PlayerId] = bot;
                Require(
                    Execute(new AddSeasonPlayer(bot.PlayerId, SimIds.Make(0x05e40000, index + 1), $"{group.Profile}-{k + 1}"), bot: null),
                    "add a player");
            }
        }

        _clock.MoveTo(_start);
        Require(Execute(new ChangeSeasonStatus(SeasonStatus.Active), bot: null), "start the season");

        Enqueue(_deadline, WakeKind.Deadline);
        var final = _deadline.AddHours(_inputs.Settings.Admin.FinalReviewHours);
        Enqueue(final, WakeKind.FinalReview);
        for (var day = 0; _start.AddDays(day) < final; day++)
        {
            foreach (var hour in _inputs.Settings.Admin.CheckHours.Distinct().Order())
            {
                var at = _start.AddDays(day).AddHours(hour);
                if (at < final)
                {
                    Enqueue(at, WakeKind.AdminCheck);
                }
            }
        }

        foreach (var bot in _players)
        {
            NextSession(bot);
        }
    }

    // The bot's free time, day by day: a session in the evening, longer at weekends, sometimes none.
    private List<Session> Sessions(BotProfile profile, SimRandom random)
    {
        var list = new List<Session>();
        for (var day = 0; day < _days; day++)
        {
            var date = _start.AddDays(day);
            var skip = random.Chance(profile.SkipDayChance);
            var noise = Math.Exp((profile.DayNoise * random.Normal()) - (profile.DayNoise * profile.DayNoise / 2));
            var jitter = (random.NextDouble() * 3) - 1.5;
            if (skip)
            {
                continue;
            }

            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var hours = Math.Min(profile.MaxSessionHours, profile.HoursPerDay * noise * (weekend ? profile.WeekendFactor : 1));
            if (hours < 0.05)
            {
                continue;
            }

            var startHour = Math.Clamp(profile.SessionStartHour - (hours / 2) + jitter, 7, 23);
            var start = date.AddHours(startHour);
            var end = start.AddHours(hours);
            if (start >= _deadline)
            {
                continue;
            }

            // Free time after the deadline is not play time of this season
            list.Add(new Session(start, end > _deadline ? (_deadline - start).TotalHours : hours));
        }

        return list;
    }

    private void NextSession(Bot bot)
    {
        bot.Session++;
        bot.Budget = 0;
        if (bot.Session >= bot.Sessions.Count)
        {
            return;
        }

        // A session that starts late (the previous one ran over) still ends at the deadline at the latest
        var session = bot.Sessions[bot.Session];
        var start = session.Start > _clock.UtcNow ? session.Start : _clock.UtcNow;
        bot.Budget = Math.Max(0, Math.Min(session.Hours, (_deadline - start).TotalHours));
        bot.FreeHours += bot.Budget;
        Enqueue(start, WakeKind.Bot, bot.Index, bot.Session);
    }

    private void BotWake(Bot bot, int session)
    {
        if (session != bot.Session || bot.Done)
        {
            return;
        }

        for (var actions = 0; actions < MaxActionsPerWake; actions++)
        {
            // A branch choice takes no time and is allowed after the deadline too (D-305): the steps belong to a throw before it
            var player = _state.Players[bot.PlayerId];
            if (player.Choice is { Kind: ChoiceKind.Branch } branch)
            {
                ChooseBranch(bot, player, branch);
                continue;
            }

            var now = _clock.UtcNow;
            if (now >= _deadline)
            {
                // No turns after the deadline: a game still being played does not count
                bot.Done = true;
                return;
            }

            if (bot.Budget <= Epsilon)
            {
                NextSession(bot);
                return;
            }

            switch (player.Phase)
            {
                case TurnPhase.Idle:
                    bot.RerollRefused = false;
                    var rolled = Execute(new RollGame(bot.PlayerId), bot);
                    if (!rolled.IsAccepted)
                    {
                        if (rolled.Rejection!.Code == RejectionCodes.TooManyUncheckedRuns)
                        {
                            WaitForAdmin(bot);
                        }
                        else
                        {
                            NextSession(bot);
                        }

                        return;
                    }

                    break;

                case TurnPhase.Rolling:
                    if (!TakeOffer(bot, player))
                    {
                        NextSession(bot);
                        return;
                    }

                    break;

                case TurnPhase.Playing:
                    var plan = bot.Plan ??= NewPlan(bot, player);
                    var left = plan.ActAt - plan.Played;
                    if (left > Epsilon)
                    {
                        var step = Math.Min(Math.Min(left, bot.Budget), (_deadline - now).TotalHours);
                        plan.Played += step;
                        bot.Budget -= step;
                        bot.PlayHours += step;
                        _runs[plan.RunId].PlayHours = plan.Played;
                        Enqueue(now.AddHours(step), WakeKind.Bot, bot.Index, bot.Session);
                        return;
                    }

                    if (!Act(bot, plan))
                    {
                        NextSession(bot);
                        return;
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Unknown phase {player.Phase}.");
            }
        }

        // The loop guard (D-355): a bot that cannot make progress gives up this session
        _guardTrips++;
        NextSession(bot);
    }

    // Rolling: «Уже проходил», a reroll, or start the game (pick the shortest of a choice). False — nothing to do now.
    private bool TakeOffer(Bot bot, SeasonPlayer player)
    {
        var behaviour = _inputs.Settings.Behaviour;
        Guid gameId;
        decimal? hours;
        ICommand start;
        if (player.Choice is { Kind: ChoiceKind.Game } choice)
        {
            var pick = choice.Options
                .Where(o => o.Game is not null)
                .OrderBy(o => o.Game!.Snapshot.Hours ?? decimal.MaxValue)
                .ThenBy(o => o.Id, StringComparer.Ordinal)
                .First();
            gameId = pick.Game!.GameId;
            hours = pick.Game.Snapshot.Hours;
            start = new MakeChoice(bot.PlayerId, choice.ChoiceId, pick.Id);
        }
        else
        {
            var offer = player.Offer ?? throw new InvalidOperationException($"Bot {bot.Index} is Rolling without an offer.");
            gameId = offer.GameId;
            hours = offer.Snapshot.Hours;
            start = new StartRun(bot.PlayerId);
        }

        if (_bots.Chance(behaviour.AlreadyPlayedChance))
        {
            return Execute(new DeclareAlreadyPlayed(bot.PlayerId, gameId), bot).IsAccepted;
        }

        if (!bot.RerollRefused && WantsReroll(bot, player, (double)(hours ?? 0)))
        {
            if (Execute(new Reroll(bot.PlayerId), bot).IsAccepted)
            {
                return true;
            }

            bot.RerollRefused = true;
        }

        return Execute(start, bot).IsAccepted;
    }

    private bool WantsReroll(Bot bot, SeasonPlayer player, double hours)
    {
        var behaviour = _inputs.Settings.Behaviour;
        if (player.RerollsThisRoll >= behaviour.MaxRerollsPerRoll)
        {
            return false;
        }

        var (payment, price) = RerollPrice.For(player, _state.Rules.Roll);
        return payment switch
        {
            RerollPayment.FreeThisRoll or RerollPayment.FreeRerollResource or RerollPayment.FreeMode =>
                (behaviour.RerollAboveHours is { } free && hours > free) || (behaviour.DeadlineAware && hours > HoursLeft(bot)),
            RerollPayment.Coins => behaviour.PaidRerollAboveHours is { } paid && hours > paid && player.Coins >= price,
            _ => false,
        };
    }

    // The free time the bot still has before the deadline: this session's rest and the sessions to come
    private static double HoursLeft(Bot bot) => bot.Budget + bot.Sessions.Skip(bot.Session + 1).Sum(s => s.Hours);

    private RunPlan NewPlan(Bot bot, SeasonPlayer player)
    {
        var behaviour = _inputs.Settings.Behaviour;
        var run = _state.Runs[player.ActiveRunId ?? throw new InvalidOperationException($"Bot {bot.Index} is Playing without a run.")];
        var hours = (double)(run.Snapshot.Hours ?? 1);
        var need = Math.Max(0.1, _bots.LogNormal(hours, behaviour.PlayTimeNoise));
        var minPlay = _state.Rules.Roll.MinPlayMinutesBeforeDrop / 60.0;
        var plan = new RunPlan(run.RunId, need);
        if (run.Snapshot.TechRerollWindowHours > 0 && _bots.Chance(behaviour.TechRerollChance))
        {
            plan.Act(PlanAction.TechReroll, Math.Min(behaviour.TechRerollAfterHours, need));
        }
        else if (bot.Dropper && hours > behaviour.DropAboveHours && minPlay < need)
        {
            plan.Act(PlanAction.Drop, minPlay);
        }
        else if (_bots.Chance(behaviour.RandomDropChance))
        {
            var share = behaviour.RandomDropFrom + ((behaviour.RandomDropTo - behaviour.RandomDropFrom) * _bots.NextDouble());
            plan.Act(PlanAction.Drop, Math.Max(minPlay, need * share));
        }

        if (plan.ActAt > need)
        {
            plan.Act(PlanAction.Complete, need);
        }

        return plan;
    }

    // The planned end of the run. False — the engine refused it; the bot tries again next session.
    private bool Act(Bot bot, RunPlan plan)
    {
        switch (plan.Action)
        {
            case PlanAction.Complete:
                var difficulty = s_difficulties[_bots.Weighted(_difficultyWeights)];
                if (!Execute(new CompleteRun(bot.PlayerId, difficulty), bot).IsAccepted)
                {
                    return false;
                }

                bot.Plan = null;
                Execute(new SubmitProof(bot.PlayerId, plan.RunId, [ProofLink]), bot);
                return true;

            case PlanAction.Drop:
                if (!Execute(new DropRun(bot.PlayerId), bot).IsAccepted)
                {
                    return false;
                }

                bot.Plan = null;
                return true;

            case PlanAction.TechReroll:
                var reason = s_techReasons[_bots.NextInt(0, s_techReasons.Length)];
                if (Execute(new TechReroll(bot.PlayerId, reason, Comment: null), bot).IsAccepted)
                {
                    bot.Plan = null;
                    bot.RerollRefused = false;
                    return true;
                }

                // Past the window only the admin could: the bot plays on to the end
                plan.Act(PlanAction.Complete, plan.Need);
                return true;

            default:
                throw new InvalidOperationException($"Unknown plan {plan.Action}.");
        }
    }

    private void ChooseBranch(Bot bot, SeasonPlayer player, PendingChoice choice)
    {
        var options = choice.Options.Select(o => o.Id).ToList();
        var pick = bot.BranchPolicy switch
        {
            BranchPolicy.Default => options[0],
            BranchPolicy.Random => options[_bots.NextInt(0, options.Count)],
            BranchPolicy.Shortest => options
                .Select((cell, order) => (cell, order, distance: _cellsToFinish!.TryGetValue(cell, out var d) ? d : int.MaxValue))
                .OrderBy(x => x.distance)
                .ThenBy(x => x.order)
                .First().cell,
            _ => throw new InvalidOperationException($"Unknown branch policy {bot.BranchPolicy}."),
        };
        _branches.Add(new BranchOutcome(bot.Index, player.CellId, pick));
        Require(Execute(new MakeChoice(bot.PlayerId, choice.ChoiceId, pick), bot), "choose a branch");
    }

    // The unchecked-runs limit holds the roll: the bot waits for the admin's next check, within its session.
    private void WaitForAdmin(Bot bot)
    {
        var now = _clock.UtcNow;
        var next = NextAdminCheck(now);
        var wait = next is { } at ? (at - now).TotalHours + (1.0 / 60) : double.MaxValue;
        if (wait >= bot.Budget)
        {
            bot.BlockedHours += bot.Budget;
            NextSession(bot);
            return;
        }

        bot.BlockedHours += wait;
        bot.Budget -= wait;
        Enqueue(now.AddHours(wait), WakeKind.Bot, bot.Index, bot.Session);
    }

    private DateTimeOffset? NextAdminCheck(DateTimeOffset now)
    {
        var hours = _inputs.Settings.Admin.CheckHours.Distinct().Order().ToList();
        for (var day = (int)Math.Floor((now - _start).TotalDays); day <= _days + 1; day++)
        {
            foreach (var hour in hours)
            {
                var at = _start.AddDays(day).AddHours(hour);
                if (at > now)
                {
                    return at;
                }
            }
        }

        return null;
    }

    // The admin bot: the proof queue in its order, proofs sent long enough ago; the final review takes everything left.
    private void AdminCheck(bool final)
    {
        var admin = _inputs.Settings.Admin;
        if (!final && _bots.Chance(admin.SkipCheckChance))
        {
            return;
        }

        var cutoff = _clock.UtcNow.AddHours(-admin.MinDelayHours);
        foreach (var runId in ProofReviewOrder.Order(_state))
        {
            var run = _state.Runs[runId];
            var sent = run.Proof?.SubmittedAt;
            if (!final && (sent is null || sent > cutoff))
            {
                continue;
            }

            if (_bots.Chance(admin.RejectChance))
            {
                Execute(new RejectProof(runId, "Пруф не подтверждает прохождение"), bot: null);
            }
            else
            {
                Execute(sent is null ? new ApproveProof(runId, Comment: "Без скрина: проверено на итогах") : new ApproveProof(runId), bot: null);
            }
        }
    }

    private CommandResult Execute(ICommand command, Bot? bot)
    {
        var result = SeasonEngine.Execute(_state, command, _context);
        _commands++;
        if (!result.IsAccepted)
        {
            var code = result.Rejection!.Code;
            _rejections[code] = _rejections.GetValueOrDefault(code) + 1;
            if (bot is not null)
            {
                bot.Rejected++;
            }

            return result;
        }

        _state = result.State;
        _events += result.Events.Count;
        foreach (var e in result.Events)
        {
            Observe(e);
        }

        return result;
    }

    private void Observe(IGameEvent e)
    {
        var days = (_clock.UtcNow - _start).TotalDays;
        switch (e)
        {
            case RunStarted started:
                var record = new RunRecord(_byPlayer[started.PlayerId].Index, (double)(started.Snapshot.Hours ?? 0), started.Snapshot.Zone?.Id);
                _runs[started.RunId] = record;
                _runOrder.Add(record);
                break;
            case RunCompleted completed:
                _runs[completed.RunId].End = RunEnd.Completed;
                _runs[completed.RunId].FreeMode = completed.FreeMode;
                _byPlayer[completed.PlayerId].Completed++;
                break;
            case RunDropped dropped:
                _runs[dropped.RunId].End = RunEnd.Dropped;
                _byPlayer[dropped.PlayerId].Drops++;
                break;
            case RunTechRerolled tech:
                _runs[tech.RunId].End = RunEnd.TechRerolled;
                _byPlayer[tech.PlayerId].TechRerolls++;
                break;
            case ProofRejected rejected:
                _runs[rejected.RunId].End = RunEnd.Rejected;
                _byPlayer[rejected.PlayerId].Completed--;
                break;
            case PointsChanged { Reason: PointsReason.CompletionRoll, RunId: { } run } points:
                _runs[run].Points += points.Delta;
                break;
            case PointsChanged { Reason: PointsReason.DropPenalty, RunId: { } run } points:
                _runs[run].Penalty -= points.Delta;
                break;
            case PointsChanged { Reason: PointsReason.ProofRejected, RunId: { } run } points:
                _runs[run].Points += points.Delta;
                break;
            case PointsChanged { Reason: PointsReason.CellBonus } points:
                _byPlayer[points.PlayerId].CellBonus += points.Delta;
                break;
            case PointsChanged { Reason: PointsReason.FinishBonus or PointsReason.FinishBonusRevoked } points:
                _byPlayer[points.PlayerId].FinishBonus += points.Delta;
                break;
            case PointsChanged points:
                _byPlayer[points.PlayerId].OtherPoints += points.Delta;
                break;
            case PlayerMoved { Reason: MoveReason.Teleport } moved:
                _byPlayer[moved.PlayerId].Teleports++;
                break;
            case GameRolled rolled:
                _misses += rolled.Misses.Count;
                break;
            case GameChoiceRolled rolled:
                _misses += rolled.Misses.Count;
                break;
            case GameRerolled rerolled:
                var bot = _byPlayer[rerolled.PlayerId];
                if (rerolled.Payment is RerollPayment.Coins or RerollPayment.BadEvent)
                {
                    bot.PaidRerolls++;
                }
                else
                {
                    bot.FreeRerolls++;
                }

                break;
            case GameExcluded { Reason: ExclusionReason.AlreadyPlayed } excluded:
                _byPlayer[excluded.PlayerId].AlreadyPlayed++;
                break;
            case PlayerFinished finished:
                _byPlayer[finished.PlayerId].FinishedDay = (finished.FinishedAt - _start).TotalDays;
                break;
            case PlayerFinishRevoked revoked:
                _byPlayer[revoked.PlayerId].FinishedDay = null;
                _byPlayer[revoked.PlayerId].FrozenDay = null;
                break;
            case PlayerFrozen frozen:
                _byPlayer[frozen.PlayerId].FrozenDay = days;
                break;
            default:
                break;
        }
    }

    private SeasonOutcome Collect()
    {
        var rows = _state.Result ?? throw new InvalidOperationException("The season has no result.");
        var byPlayer = rows.ToDictionary(r => r.PlayerId);
        foreach (var bot in _players.Where(b => b.Plan is not null))
        {
            _runs[bot.Plan!.RunId].End = RunEnd.Unfinished;
        }

        var bots = _players.Select(b =>
        {
            var row = byPlayer[b.PlayerId];
            var finish = _state.Players[b.PlayerId].Finish;
            return new BotOutcome(
                b.Index,
                b.Profile,
                b.Dropper,
                b.BranchPolicy,
                row.Points,
                row.Place,
                row.IsFirst,
                finish?.Order,
                finish is null ? null : b.FinishedDay,
                b.Completed,
                b.Drops,
                b.TechRerolls,
                b.AlreadyPlayed,
                b.FreeRerolls,
                b.PaidRerolls,
                b.PlayHours,
                b.FreeHours,
                b.BlockedHours,
                b.Rejected,
                b.CellBonus,
                b.FinishBonus,
                b.OtherPoints,
                b.Teleports);
        }).ToList();

        var first = _players.FirstOrDefault(b => byPlayer[b.PlayerId].IsFirst);
        return new SeasonOutcome(
            _index,
            _seed,
            bots,
            [.. _runOrder.Select(r => new RunOutcome(r.Bot, r.Hours, r.PlayHours, r.End, r.Points, r.Penalty, r.Zone, r.FreeMode))],
            [.. _branches],
            first?.FinishedDay,
            first?.FrozenDay,
            new SortedDictionary<string, int>(_rejections, StringComparer.Ordinal),
            _commands,
            _events,
            _misses,
            _guardTrips);
    }

    private static void Require(CommandResult result, string what)
    {
        if (!result.IsAccepted)
        {
            throw new InvalidOperationException($"The simulation could not {what}: {result.Rejection!.Code} {result.Rejection.Detail}");
        }
    }

    private void Enqueue(DateTimeOffset at, WakeKind kind, int bot = -1, int session = -1) =>
        _queue.Enqueue(new Wake(kind, bot, session), (at.UtcTicks, (int)kind, _seq++));

    private readonly record struct Wake(WakeKind Kind, int Bot, int Session);

    private readonly record struct Session(DateTimeOffset Start, double Hours);

    private enum PlanAction
    {
        Complete,
        Drop,
        TechReroll,
    }

    private sealed class RunPlan(Guid runId, double need)
    {
        public Guid RunId { get; } = runId;

        public double Need { get; } = need;

        public PlanAction Action { get; private set; } = PlanAction.Complete;

        public double ActAt { get; private set; } = need;

        public double Played { get; set; }

        public void Act(PlanAction action, double at)
        {
            Action = action;
            ActAt = at;
        }
    }

    private sealed class RunRecord(int bot, double hours, string? zone)
    {
        public int Bot { get; } = bot;

        public double Hours { get; } = hours;

        public string? Zone { get; } = zone;

        public double PlayHours { get; set; }

        public RunEnd End { get; set; } = RunEnd.Unfinished;

        public int Points { get; set; }

        public int Penalty { get; set; }

        public bool FreeMode { get; set; }
    }

    private sealed class Bot(int index, Guid playerId, string profile, bool dropper, BranchPolicy branchPolicy, List<Session> sessions)
    {
        public int Index { get; } = index;

        public Guid PlayerId { get; } = playerId;

        public string Profile { get; } = profile;

        public bool Dropper { get; } = dropper;

        public BranchPolicy BranchPolicy { get; } = branchPolicy;

        public List<Session> Sessions { get; } = sessions;

        public int Session { get; set; } = -1;

        public double Budget { get; set; }

        public bool Done { get; set; }

        public bool RerollRefused { get; set; }

        public RunPlan? Plan { get; set; }

        public int Completed { get; set; }

        public int Drops { get; set; }

        public int TechRerolls { get; set; }

        public int AlreadyPlayed { get; set; }

        public int FreeRerolls { get; set; }

        public int PaidRerolls { get; set; }

        public double PlayHours { get; set; }

        public double FreeHours { get; set; }

        public double BlockedHours { get; set; }

        public int Rejected { get; set; }

        public int CellBonus { get; set; }

        public int FinishBonus { get; set; }

        public int OtherPoints { get; set; }

        public int Teleports { get; set; }

        public double? FinishedDay { get; set; }

        public double? FrozenDay { get; set; }
    }
}
