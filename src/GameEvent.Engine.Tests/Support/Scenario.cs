using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

namespace GameEvent.Engine.Tests.Support;

/// <summary>
/// Builder for engine scenarios, so a test reads like a description of a game:
/// <code>
/// var s = Scenario.New()
///     .WithCategory("Horror").WithGame("Silent Hill", hours: 12, "Horror")
///     .WithPlayers("Вася", "Петя");
/// s.Roll("Вася").Start("Вася").NextRandom(3, 4, 1, 2).Complete("Вася", Difficulty.Normal);
/// </code>
/// Setup steps throw if the engine rejects them; game actions record the result in <see cref="Last"/>.
/// </summary>
public sealed class Scenario
{
    private const uint PlayerIdPrefix = 0x10000000;
    private const uint GameIdPrefix = 0x20000000;
    private const uint SeasonIdPrefix = 0x30000000;
    private const uint UserIdPrefix = 0x40000000;

    private readonly List<Game> _games = [];
    private readonly List<Category> _categories = [];
    private readonly Dictionary<string, Guid> _players = [];
    private readonly List<IGameEvent> _log = [];
    private Ruleset _initialRuleset;
    private bool _startSeason = true;
    private bool _expectRejection;

    private Scenario(Ruleset ruleset, int seed)
    {
        _initialRuleset = ruleset;
        Random = new ScriptedRandom(seed);
    }

    /// <summary>The rules in force: the season's once it exists, the ones it will be created with before that.</summary>
    public Ruleset Ruleset => State.Ruleset ?? _initialRuleset;

    public FixedClock Clock { get; } = new(FixedClock.SeasonStart);

    public ScriptedRandom Random { get; }

    public SequentialIds Ids { get; } = new();

    public SeasonState State { get; private set; } = SeasonState.Empty;

    /// <summary>All accepted events so far, in order.</summary>
    public IReadOnlyList<IGameEvent> Log => _log;

    /// <summary>Result of the last executed command.</summary>
    public CommandResult Last { get; private set; } = null!;

    /// <summary>
    /// A scenario on the pinned test ruleset (<see cref="TestRuleset"/>), not on docs/ruleset.default.json:
    /// default numbers are temporary and rebalancing must not break tests of mechanics.
    /// </summary>
    public static Scenario New(Ruleset? ruleset = null, int seed = 42) => new(ruleset ?? TestRuleset.Create(), seed);

    /// <summary>
    /// Before the season exists: the rules it is created with. After: the admin changes the rules
    /// (a <see cref="ChangeRuleset"/> command, so the change is in the log).
    /// </summary>
    public Scenario WithRuleset(Func<Ruleset, Ruleset> change)
    {
        if (!State.IsCreated)
        {
            _initialRuleset = change(_initialRuleset);
            return this;
        }

        Setup(new ChangeRuleset(change(Ruleset)));
        return this;
    }

    public Scenario WithMapLength(int length) =>
        WithRuleset(r => r with { Map = r.Map with { LinearLength = length } });

    public Scenario WithCategory(string name, int weight = 1)
    {
        _categories.Add(new Category(name, weight));
        return this;
    }

    public Scenario WithGame(string title, decimal? hours, params string[] tags)
    {
        _games.Add(new Game(SequentialIds.Make(GameIdPrefix, _games.Count + 1), title, [.. tags], hours));
        return this;
    }

    /// <summary>A soft-deleted game: it stays in the pool view but must never be rolled.</summary>
    public Scenario WithDeletedGame(string title, decimal? hours, params string[] tags)
    {
        _games.Add(new Game(SequentialIds.Make(GameIdPrefix, _games.Count + 1), title, [.. tags], hours, IsDeleted: true));
        return this;
    }

    /// <summary>The admin corrects the hours of a game in the pool (outside the season log).</summary>
    public Scenario ChangePoolHours(string title, decimal? hours)
    {
        var index = _games.FindIndex(g => g.Title == title);
        if (index < 0)
        {
            throw new KeyNotFoundException($"No game '{title}' in the pool.");
        }

        _games[index] = _games[index] with { Hours = hours };
        return this;
    }

    /// <summary>The admin soft-deletes a game from the pool (outside the season log).</summary>
    public Scenario DeleteGame(string title)
    {
        var index = _games.FindIndex(g => g.Title == title);
        if (index < 0)
        {
            throw new KeyNotFoundException($"No game '{title}' in the pool.");
        }

        _games[index] = _games[index] with { IsDeleted = true };
        return this;
    }

    /// <summary>Moves the scenario clock forward.</summary>
    public Scenario Advance(TimeSpan by)
    {
        Clock.Advance(by);
        return this;
    }

    /// <summary>The season stays a draft after creation (for lifecycle tests).</summary>
    public Scenario AsDraft()
    {
        _startSeason = false;
        return this;
    }

    /// <summary>Creates the season if needed (see <see cref="AsDraft"/>) and adds players by name.</summary>
    public Scenario WithPlayers(params string[] names)
    {
        EnsureSeason();
        foreach (var name in names)
        {
            var id = SequentialIds.Make(PlayerIdPrefix, _players.Count + 1);
            _players.Add(name, id);
            Setup(new AddSeasonPlayer(id, SequentialIds.Make(UserIdPrefix, _players.Count), name));
        }

        return this;
    }

    /// <summary>Values the random source returns next, e.g. dice faces.</summary>
    public Scenario NextRandom(params int[] values)
    {
        Random.Enqueue(values);
        return this;
    }

    /// <summary>
    /// The next game action may be rejected. Without it a rejected Roll, Start or Complete throws,
    /// so a refusal in the middle of a chain cannot hide behind the last step's assertion.
    /// </summary>
    public Scenario ExpectRejection()
    {
        _expectRejection = true;
        return this;
    }

    public Scenario Roll(string player) => Play(new RollGame(PlayerId(player)));

    public Scenario Start(string player) => Play(new StartRun(PlayerId(player)));

    public Scenario Complete(
        string player,
        Difficulty difficulty = Difficulty.Normal,
        decimal? estimatedHours = null,
        string? hoursSource = null,
        bool challengeDone = false,
        RunReview? review = null) =>
        Play(new CompleteRun(PlayerId(player), difficulty, estimatedHours, hoursSource, challengeDone, review));

    /// <summary>The player reviews one of their runs (or changes the review) with <see cref="ReviewRun"/>.</summary>
    public Scenario Review(string player, Guid runId, int rating, string? text = null) =>
        Play(new ReviewRun(PlayerId(player), runId, new RunReview(rating, text)));

    /// <summary>
    /// Walks the season forward to <paramref name="status"/> by the admin's status commands. Since C10 (D-101) the season
    /// finishes only with an empty proof queue; tests of what a finished season refuses may need one with an unchecked
    /// run, so when the engine refuses the finish with <c>season.proofsPending</c> the finish is appended as a crafted log
    /// — the events the finish writes (<see cref="SeasonStatusChanged"/> and the result), replayed into the state.
    /// </summary>
    public Scenario MoveStatusTo(SeasonStatus status)
    {
        while (State.Status < status)
        {
            var next = State.Status + 1;
            Act(new ChangeSeasonStatus(next));
            if (!Last.IsAccepted && next == SeasonStatus.Finished && Last.Rejection!.Code == RejectionCodes.SeasonProofsPending)
            {
                AppendCraftedEvents(
                    new SeasonStatusChanged(SeasonStatus.Closing, SeasonStatus.Finished),
                    new SeasonResultRecorded(Leaderboard.Build(State)));
                continue;
            }

            if (!Last.IsAccepted)
            {
                throw new InvalidOperationException($"Moving the season to {next} was rejected: {Last.Rejection}");
            }
        }

        return this;
    }

    /// <summary>Appends events straight to the log, as if an earlier command had written them, and replays the state.</summary>
    public Scenario AppendCraftedEvents(params IGameEvent[] events)
    {
        _log.AddRange(events);
        State = SeasonEngine.Replay(_log);
        return this;
    }

    /// <summary>Executes any command and records the result; never throws on rejection.</summary>
    public Scenario Act(ICommand command)
    {
        Last = SeasonEngine.Execute(State, command, Context());
        if (Last.IsAccepted)
        {
            _log.AddRange(Last.Events);
            State = Last.State;
        }

        return this;
    }

    public Guid PlayerId(string name) =>
        _players.TryGetValue(name, out var id) ? id : throw new KeyNotFoundException($"No player '{name}' in the scenario.");

    public Guid GameId(string title) =>
        _games.SingleOrDefault(g => g.Title == title)?.Id ?? throw new KeyNotFoundException($"No game '{title}' in the pool.");

    public SeasonPlayer Player(string name) => State.Players[PlayerId(name)];

    /// <summary>Name of the player with the given id.</summary>
    public string PlayerName(Guid id) => _players.Single(p => p.Value == id).Key;

    /// <summary>Title of the game with the given id.</summary>
    public string GameTitle(Guid id) => _games.Single(g => g.Id == id).Title;

    /// <summary>Events of the last command of the given type.</summary>
    public IEnumerable<T> LastEvents<T>() where T : IGameEvent => Last.Events.OfType<T>();

    public EngineContext Context() =>
        new(Clock, Random, Ids, new PoolSnapshot([.. _games], [.. _categories]));

    private Scenario Play(ICommand command)
    {
        var mayBeRejected = _expectRejection;
        _expectRejection = false;
        Act(command);
        if (!Last.IsAccepted && !mayBeRejected)
        {
            throw new InvalidOperationException(
                $"{command} was rejected: {Last.Rejection}. Call ExpectRejection() first if the test expects that.");
        }

        return this;
    }

    private void EnsureSeason()
    {
        if (!State.IsCreated)
        {
            Setup(new CreateSeason(SequentialIds.Make(SeasonIdPrefix, 1), "Тестовый сезон", _initialRuleset));

            // Scenarios play: the season starts right away unless a test drives the lifecycle itself.
            if (_startSeason)
            {
                Setup(new ChangeSeasonStatus(SeasonStatus.Active));
            }
        }
    }

    private void Setup(ICommand command)
    {
        Act(command);
        if (!Last.IsAccepted)
        {
            throw new InvalidOperationException($"Scenario setup {command} was rejected: {Last.Rejection}");
        }
    }
}
