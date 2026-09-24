using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
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

    private readonly List<PoolGame> _games = [];
    private readonly List<PoolCategory> _categories = [];
    private readonly Dictionary<string, Guid> _players = [];
    private readonly List<IGameEvent> _log = [];

    private Scenario(Ruleset ruleset) => Ruleset = ruleset;

    public Ruleset Ruleset { get; private set; }

    public FixedClock Clock { get; } = new(FixedClock.SeasonStart);

    public ScriptedRandom Random { get; } = new();

    public SequentialIds Ids { get; } = new();

    public SeasonState State { get; private set; } = SeasonState.Empty;

    /// <summary>All accepted events so far, in order.</summary>
    public IReadOnlyList<IGameEvent> Log => _log;

    /// <summary>Result of the last executed command.</summary>
    public CommandResult Last { get; private set; } = null!;

    /// <summary>A scenario on the default ruleset (docs/ruleset.default.json).</summary>
    public static Scenario New(Ruleset? ruleset = null) => new(ruleset ?? RulesetJson.Default());

    public Scenario WithRuleset(Func<Ruleset, Ruleset> change)
    {
        Ruleset = change(Ruleset);
        return this;
    }

    public Scenario WithMapLength(int length) =>
        WithRuleset(r => r with { Map = r.Map with { LinearLength = length } });

    public Scenario WithCategory(string name, int weight = 1)
    {
        _categories.Add(new PoolCategory(name, weight));
        return this;
    }

    public Scenario WithGame(string title, decimal? hours, params string[] tags)
    {
        _games.Add(new PoolGame(SequentialIds.Make(GameIdPrefix, _games.Count + 1), title, [.. tags], hours));
        return this;
    }

    /// <summary>Creates the season if needed and adds players by name.</summary>
    public Scenario WithPlayers(params string[] names)
    {
        EnsureSeason();
        foreach (var name in names)
        {
            var id = SequentialIds.Make(PlayerIdPrefix, _players.Count + 1);
            _players.Add(name, id);
            Setup(new AddPlayer(id, name));
        }

        return this;
    }

    /// <summary>Values the random source returns next, e.g. dice faces.</summary>
    public Scenario NextRandom(params int[] values)
    {
        Random.Enqueue(values);
        return this;
    }

    public Scenario Roll(string player) => Act(new RollGame(PlayerId(player)));

    public Scenario Start(string player) => Act(new StartRun(PlayerId(player)));

    public Scenario Complete(string player, Difficulty difficulty = Difficulty.Normal, decimal? estimatedHours = null) =>
        Act(new CompleteRun(PlayerId(player), difficulty, estimatedHours));

    /// <summary>Executes any command and records the result.</summary>
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

    public PlayerState Player(string name) => State.Players[PlayerId(name)];

    /// <summary>Events of the last command of the given type.</summary>
    public IEnumerable<T> LastEvents<T>() where T : IGameEvent => Last.Events.OfType<T>();

    public EngineContext Context() =>
        new(Clock, Random, Ids, Ruleset, new PoolSnapshot([.. _games], [.. _categories]));

    private void EnsureSeason()
    {
        if (!State.IsCreated)
        {
            Setup(new CreateSeason(SequentialIds.Make(SeasonIdPrefix, 1)));
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
