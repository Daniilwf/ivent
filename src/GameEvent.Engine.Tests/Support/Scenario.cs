using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Ranking;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;
using GameEvent.Engine.Undo;

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
/// Every builder call is also kept as C# (<see cref="ToCode"/>), so a failing random game prints as a scenario (C13).
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
    private readonly List<LoggedCommand> _history = [];
    private Ruleset _initialRuleset;
    private bool _startSeason = true;
    private bool _expectRejection;

    private Scenario(Ruleset ruleset, int seed)
    {
        _initialRuleset = ruleset;
        Random = new ScriptedRandom(seed);
        _code.Add(() => $"var s = Scenario.New(seed: {seed});");
        var pinned = TestRuleset.Create();
        if (ruleset != pinned)
        {
            _code.Add(() => $"s.WithRuleset(r => {ScenarioCode.With("r", pinned, ruleset, CodeName)});");
        }
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
        ArgumentNullException.ThrowIfNull(change);
        var before = Ruleset;
        var after = change(before);
        using var _ = Recording(() => before == after ? null : $"s.WithRuleset(r => {ScenarioCode.With("r", before, after, CodeName)});");
        if (!State.IsCreated)
        {
            _initialRuleset = after;
            return this;
        }

        Setup(new ChangeRuleset(after));
        return this;
    }

    public Scenario WithMapLength(int length)
    {
        using var _ = Recording(() => $"s.WithMapLength({length});");
        return WithRuleset(r => r with { Map = r.Map with { LinearLength = length } });
    }

    /// <summary>
    /// A graph map (D-300): before the season exists, it is created in the graph mode with this map; after, the admin
    /// publishes it (<see cref="PublishMap"/>, which must be accepted).
    /// </summary>
    public Scenario WithMap(MapGraph map)
    {
        ArgumentNullException.ThrowIfNull(map);
        using var _ = Recording(() => $"s.WithMap({ScenarioCode.Value(map, CodeName)});");
        if (!State.IsCreated)
        {
            _initialRuleset = _initialRuleset with { Features = _initialRuleset.Features with { MapMode = MapMode.Graph } };
            _initialMap = map;
            return this;
        }

        Setup(new PublishMap(map, "Новая карта"));
        return this;
    }

    private MapGraph? _initialMap;

    /// <summary>The player picks the branch into <paramref name="cellId"/> at the fork they wait at.</summary>
    public Scenario ChooseBranch(string player, string cellId) =>
        Play(new MakeChoice(PlayerId(player), Player(player).Choice?.ChoiceId ?? Guid.Empty, cellId));

    public Scenario WithCategory(string name, int weight = 1)
    {
        using var _ = Recording(() => $"s.WithCategory({ScenarioCode.Literal(name)}, weight: {weight});");
        _categories.Add(new Category(name, weight));
        return this;
    }

    public Scenario WithGame(string title, decimal? hours, params string[] tags)
    {
        using var _ = Recording(() => $"s.WithGame({GameCode(title, hours, tags)});");
        _games.Add(new Game(SequentialIds.Make(GameIdPrefix, _games.Count + 1), title, [.. tags], hours));
        return this;
    }

    /// <summary>A soft-deleted game: it stays in the pool view but must never be rolled.</summary>
    public Scenario WithDeletedGame(string title, decimal? hours, params string[] tags)
    {
        using var _ = Recording(() => $"s.WithDeletedGame({GameCode(title, hours, tags)});");
        _games.Add(new Game(SequentialIds.Make(GameIdPrefix, _games.Count + 1), title, [.. tags], hours, IsDeleted: true));
        return this;
    }

    /// <summary>The admin corrects the hours of a game in the pool (outside the season log).</summary>
    public Scenario ChangePoolHours(string title, decimal? hours)
    {
        using var _ = Recording(() => $"s.ChangePoolHours({ScenarioCode.Literal(title)}, {ScenarioCode.Value(hours, CodeName)});");
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
        using var _ = Recording(() => $"s.DeleteGame({ScenarioCode.Literal(title)});");
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
        using var _ = Recording(() => "s.AsDraft();");
        _startSeason = false;
        return this;
    }

    /// <summary>Creates the season if needed (see <see cref="AsDraft"/>) and adds players by name.</summary>
    public Scenario WithPlayers(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        using var _ = Recording(() => $"s.WithPlayers({string.Join(", ", names.Select(ScenarioCode.Literal))});");
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
        ArgumentNullException.ThrowIfNull(values);
        using var _ = Recording(() => $"s.NextRandom({string.Join(", ", values)});");
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

    /// <summary>
    /// Rolls <paramref name="title"/> for the player: the scripted wheel lands on the first category (a pool of one
    /// category of weight 1) and the draw on the game, counted among the games the player sees (not deleted, not
    /// excluded for them) in pool order.
    /// </summary>
    public Scenario RollTitle(string player, string title)
    {
        var excluded = State.IsCreated && State.Players.TryGetValue(PlayerId(player), out var p)
            ? p.Exclusions.Select(x => x.GameId).ToHashSet()
            : [];
        var index = _games.Where(g => !g.IsDeleted && !excluded.Contains(g.Id)).OrderBy(g => g.Id).ToList().FindIndex(g => g.Title == title);
        if (index < 0)
        {
            throw new KeyNotFoundException($"No game '{title}' the player sees in the pool.");
        }

        return NextRandom(0, index).Roll(player);
    }

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

    /// <summary>Walks the season forward to <paramref name="status"/> by the admin's status commands; every step must be accepted.</summary>
    public Scenario MoveStatusTo(SeasonStatus status)
    {
        while (State.Status < status)
        {
            var next = State.Status + 1;
            Act(new ChangeSeasonStatus(next));
            if (!Last.IsAccepted)
            {
                throw new InvalidOperationException($"Moving the season to {next} was rejected: {Last.Rejection}");
            }
        }

        return this;
    }

    /// <summary>
    /// Like <see cref="MoveStatusTo"/>, but a finish the engine refuses with <c>season.proofsPending</c> is appended as a
    /// crafted log (the status change and the result). Since C10 (D-101) the engine never finishes with unchecked runs;
    /// only tests of what a finished season refuses to such a run use this, and say so by calling it.
    /// </summary>
    public Scenario MoveStatusToForcingFinish(SeasonStatus status)
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
        ArgumentNullException.ThrowIfNull(events);
        using var _ = Recording(() => $"s.AppendCraftedEvents({string.Join(", ", events.Select(e => ScenarioCode.Value(e, CodeName)))});");
        _log.AddRange(events);
        _history.Add(new LoggedCommand(SequentialIds.Make(0x7E000000, _history.Count), [.. events]));
        _commands.Add(null);
        State = SeasonEngine.Replay(_log);
        return this;
    }

    /// <summary>Executes any command and records the result; never throws on rejection.</summary>
    public Scenario Act(ICommand command)
    {
        // A ruleset change prints as a with expression on the rules in force when it ran
        var rules = Ruleset;
        using var _ = Recording(() => $"s.Act({CommandCode(command, rules)});");
        Last = SeasonEngine.Execute(State, command, Context());
        if (Last.IsAccepted)
        {
            _log.AddRange(Last.Events);
            State = Last.State;
            LastCommandId = SequentialIds.Make(0x7C000000, _history.Count);
            _history.Add(new LoggedCommand(LastCommandId, [.. Last.Events]));
            _commands.Add(command);
        }

        return this;
    }

    /// <summary>The id the last accepted command was logged under (D-104: an undo names it).</summary>
    public Guid LastCommandId { get; private set; }

    /// <summary>The season's log by command, as the queue gives it to an undo.</summary>
    public IReadOnlyList<LoggedCommand> History => _history;

    /// <summary>The accepted command behind each entry of <see cref="History"/> (null for crafted events).</summary>
    public IReadOnlyList<ICommand?> Commands => _commands;

    private readonly List<ICommand?> _commands = [];

    /// <summary>
    /// The log as the rules see it (D-104, invariant 13): the events of the commands that are not undone, without the
    /// undos themselves. The same list as <see cref="Log"/> while nothing was undone.
    /// </summary>
    public IReadOnlyList<IGameEvent> EffectiveLog
    {
        get
        {
            if (!_log.Any(e => e is CommandUndone))
            {
                return _log;
            }

            if (_effective is { } cached && cached.Commands == _history.Count)
            {
                return cached.Log;
            }

            var undone = UndoneCommands();
            List<IGameEvent> log = [.. _history.Where(c => IsEffective(c, undone)).SelectMany(c => c.Events)];
            _effective = (_history.Count, log);
            return log;
        }
    }

    private (int Commands, List<IGameEvent> Log)? _effective;

    /// <summary>The ids of the commands undone so far.</summary>
    public HashSet<Guid> UndoneCommands() =>
        [.. _history.SelectMany(c => c.Events.OfType<CommandUndone>()).Select(u => u.CommandId)];

    /// <summary>Whether a logged command counts: it is not undone (by <paramref name="undone"/>) and is not an undo itself.</summary>
    public static bool IsEffective(LoggedCommand command, IReadOnlySet<Guid> undone)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(undone);
        return !undone.Contains(command.CommandId) && !command.Events.Any(e => e is CommandUndone);
    }

    public Guid PlayerId(string name) =>
        _players.TryGetValue(name, out var id) ? id : throw new KeyNotFoundException($"No player '{name}' in the scenario.");

    public Guid GameId(string title) =>
        _games.SingleOrDefault(g => g.Title == title)?.Id ?? throw new KeyNotFoundException($"No game '{title}' in the pool.");

    public SeasonPlayer Player(string name) => State.Players[PlayerId(name)];

    /// <summary>Name of the player with the given id (the id itself for a player the builder did not add).</summary>
    public string PlayerName(Guid id) => _players.FirstOrDefault(p => p.Value == id).Key ?? id.ToString();

    /// <summary>Title of the game with the given id (the id itself for a game not in the pool).</summary>
    public string GameTitle(Guid id) => _games.FirstOrDefault(g => g.Id == id)?.Title ?? id.ToString();

    /// <summary>Events of the last command of the given type.</summary>
    public IEnumerable<T> LastEvents<T>() where T : IGameEvent => Last.Events.OfType<T>();

    public EngineContext Context() =>
        new(Clock, Random, Ids, new PoolSnapshot([.. _games], [.. _categories]), _triggers, _history);

    private IReadOnlyList<Engine.Effects.ITriggerHandler> _triggers = [];

    /// <summary>Test trigger handlers that react to every later command (D-24: stage 1 checks the chain limits with them).</summary>
    public Scenario WithTriggers(params Engine.Effects.ITriggerHandler[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        using var _ = Recording(() => $"s.WithTriggers(/* {string.Join(", ", handlers.Select(h => h.GetType().Name))} */);");
        _triggers = handlers;
        return this;
    }

    // ---- The scenario as builder code (C13): a failing random game prints as the calls that replay it ----

    private readonly List<Func<string?>> _code = [];
    private int _codeDepth;
    private DateTimeOffset _codeTime = FixedClock.SeasonStart;

    /// <summary>The builder calls made so far as C# statements, clock moves included; ids by name where the scenario knows them.</summary>
    public string ToCode() => string.Join(Environment.NewLine, _code.Select(line => line()).OfType<string>());

    /// <summary>
    /// Runs <paramref name="play"/>; a failure is rethrown with <see cref="ToCode"/> of the scenario so far. Under FsCheck the
    /// shrunk game is the one reported, so the message is the minimal failing scenario as builder code.
    /// </summary>
    public Scenario Explained(Action<Scenario> play)
    {
        ArgumentNullException.ThrowIfNull(play);
        try
        {
            play(this);
        }
        catch (Exception e) when (e is not ScenarioFailedException)
        {
            throw new ScenarioFailedException(ToCode(), e);
        }

        return this;
    }

    /// <summary>Records <paramref name="line"/> (null: nothing to print) unless an outer builder call records itself (nested calls are part of it).</summary>
    private CodeScope Recording(Func<string?> line)
    {
        if (_codeDepth == 0)
        {
            // The clock is moved directly as well (s.Clock.Advance): every recorded call carries the time it ran at
            if (Clock.UtcNow != _codeTime)
            {
                var by = Clock.UtcNow - _codeTime;
                _code.Add(() => $"s.Advance({ScenarioCode.Span(by)});");
                _codeTime = Clock.UtcNow;
            }

            _code.Add(line);
        }

        _codeDepth++;
        return new CodeScope(this);
    }

    private readonly struct CodeScope(Scenario scenario) : IDisposable
    {
        public void Dispose() => scenario._codeDepth--;
    }

    private string? CodeName(Guid id) =>
        _players.FirstOrDefault(p => p.Value == id).Key is { } player ? $"s.PlayerId({ScenarioCode.Literal(player)})"
        : _games.FirstOrDefault(g => g.Id == id) is { } game ? $"s.GameId({ScenarioCode.Literal(game.Title)})"
        : State.Runs.TryGetValue(id, out var run) ? $"Guid.Parse(\"{id}\") /* run: {PlayerName(run.PlayerId)}, {GameTitle(run.GameId)} */"
        : null;

    private string CommandCode(ICommand command, Ruleset rules) =>
        command is ChangeRuleset change
            ? $"new ChangeRuleset({ScenarioCode.With("s.Ruleset", rules, change.Ruleset, CodeName)}{(change.ExpectedVersion is { } version ? $", {version}" : "")})"
            : ScenarioCode.Value(command, CodeName);

    private static string GameCode(string title, decimal? hours, string[] tags) =>
        string.Join(", ", [ScenarioCode.Literal(title), ScenarioCode.Value(hours, _ => null), .. tags.Select(ScenarioCode.Literal)]);

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
            Setup(new CreateSeason(SequentialIds.Make(SeasonIdPrefix, 1), "Тестовый сезон", _initialRuleset, Map: _initialMap));

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

/// <summary>A scenario failed; the message starts with the scenario as builder code (C13), then the original failure.</summary>
public sealed class ScenarioFailedException(string code, Exception inner)
    : Xunit.Sdk.XunitException(
        $"The failing scenario as builder code:{Environment.NewLine}{code}{Environment.NewLine}{Environment.NewLine}{inner.Message}", inner)
{
    public string Code { get; } = code;
}
