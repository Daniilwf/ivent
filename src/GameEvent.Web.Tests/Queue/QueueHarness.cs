using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameEvent.Web.Tests.Queue;

/// <summary>
/// A real SQLite file in WAL mode with the command queue running over it. Each harness has its own file.
/// </summary>
internal sealed class QueueHarness : IAsyncDisposable
{
    private readonly string _directory;
    private readonly PooledDbContextFactory<GameEventDbContext> _factory;
    private CommandProcessor? _processor;

    private QueueHarness(string directory, IEnumerable<IInterceptor> interceptors)
    {
        _directory = directory;
        ConnectionString = $"Data Source={Path.Combine(directory, "test.db")};Pooling=False";
        var builder = new DbContextOptionsBuilder<GameEventDbContext>();
        SqliteDatabase.Configure(builder, ConnectionString);
        builder.AddInterceptors(interceptors);
        _factory = new PooledDbContextFactory<GameEventDbContext>(builder.Options);
    }

    public string ConnectionString { get; }

    public CommandBus Bus { get; private set; } = new();

    /// <summary>Starts at a moment with sub-millisecond ticks: storage must keep full precision.</summary>
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567));

    public static async Task<QueueHarness> StartAsync(params IInterceptor[] interceptors)
    {
        var directory = Path.Combine(Path.GetTempPath(), "game-event-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var harness = new QueueHarness(directory, interceptors);
        await SqliteDatabase.MigrateAsync(harness.ConnectionString);
        await harness.SeedPoolAsync();
        await harness.StartProcessorAsync();
        return harness;
    }

    public GameEventDbContext NewDb() => _factory.CreateDbContext();

    public Task<CommandOutcome> SendAsync(ICommand command, Guid seasonId, Guid? commandId = null) =>
        SendCancellableAsync(command, seasonId, commandId ?? Guid.NewGuid(), TestContext.Current.CancellationToken);

    public Task<CommandOutcome> SendCancellableAsync(ICommand command, Guid seasonId, Guid commandId, CancellationToken ct) =>
        Bus.SendAsync(new CommandEnvelope(commandId, seasonId, command, AuthorId: null), ct);

    public Task StopAsync() => StopProcessorAsync();

    /// <summary>Stops the processor and starts a fresh one over the same file: the in-memory cache is gone.</summary>
    public async Task RestartAsync()
    {
        await StopProcessorAsync();
        Bus = new CommandBus();
        await StartProcessorAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopProcessorAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file may still be locked on Windows; the temp folder is cleaned up by the OS.
        }
    }

    private async Task StartProcessorAsync()
    {
        _processor = new CommandProcessor(
            Bus, _factory, Clock, new SeededRandom(7), new CountingIds(), new Accounts.Passwords(), [], NullLogger<CommandProcessor>.Instance);
        await _processor.StartAsync(CancellationToken.None);
    }

    private async Task StopProcessorAsync()
    {
        if (_processor is not null)
        {
            await _processor.StopAsync(CancellationToken.None);
            _processor.Dispose();
            _processor = null;
        }
    }

    private async Task SeedPoolAsync()
    {
        await using var db = NewDb();
        db.Categories.AddRange(new CategoryRecord { Name = "Horror", Weight = 2 }, new CategoryRecord { Name = "Puzzle", Weight = 1 });
        db.Games.AddRange(
            new GameRecord { Id = Guid.NewGuid(), Title = "Silent Hill", TagsJson = PoolReader.TagsToJson(["Horror"]), Hours = 12 },
            new GameRecord { Id = Guid.NewGuid(), Title = "Alan Wake", TagsJson = PoolReader.TagsToJson(["Horror"]), Hours = 15 },
            new GameRecord { Id = Guid.NewGuid(), Title = "Tetris", TagsJson = PoolReader.TagsToJson(["Puzzle"]), Hours = 2 },
            new GameRecord { Id = Guid.NewGuid(), Title = "Portal", TagsJson = PoolReader.TagsToJson(["Puzzle"]), Hours = 4 });
        await db.SaveChangesAsync();
    }
}

/// <summary>The tests' clock: set by the tests, and movable by the test endpoints like the Development one (D-120).</summary>
internal sealed class TestClock : GameEvent.Infrastructure.Kernel.IAdjustableClock
{
    private readonly DateTimeOffset _start;

    public TestClock(DateTimeOffset start)
    {
        _start = start;
        UtcNow = start;
    }

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;

    public void MoveTo(DateTimeOffset at) => UtcNow = at;

    public void Reset() => UtcNow = _start;
}

internal sealed class SeededRandom(int seed) : GameEvent.Infrastructure.Kernel.IReseedableRandom
{
#pragma warning disable CA5394 // deterministic randomness is the point in tests
    private Random _random = new(seed);

    public int NextInt(int minInclusive, int maxExclusive) => _random.Next(minInclusive, maxExclusive);

    public void Seed(int? value) => _random = new Random(value ?? seed);
#pragma warning restore CA5394
}

internal sealed class CountingIds : IIdGenerator
{
    private long _next;

    public Guid NewId() => Guid.Parse($"00000000-0000-0000-0000-{Interlocked.Increment(ref _next):x12}");
}

/// <summary>
/// While <see cref="Armed"/> is set, fails right after the SQL of SaveChanges ran but before the transaction
/// commits: events and projection rows are already written inside the transaction, then the command crashes.
/// </summary>
internal sealed class FailingSaveInterceptor : SaveChangesInterceptor
{
    public bool Armed { get; set; }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) =>
        Armed ? throw new InvalidOperationException("Simulated crash after writing, before commit.") : base.SavedChangesAsync(eventData, result, cancellationToken);
}

/// <summary>
/// While <see cref="Armed"/> is set, throws right after the transaction really committed: the caller sees
/// a failure although the command is in the database (a lost connection at the worst moment).
/// </summary>
internal sealed class FailingAfterCommitInterceptor : DbTransactionInterceptor
{
    public bool Armed { get; set; }

    public override Task TransactionCommittedAsync(
        System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
        Armed ? throw new InvalidOperationException("Simulated failure after commit.") : base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
}

/// <summary>While <see cref="Armed"/> is set, SaveChanges waits until <see cref="Release"/> or until the processor is stopped.</summary>
internal sealed class BlockingSaveInterceptor : SaveChangesInterceptor
{
    public bool Armed { get; set; }

    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release()
    {
        Armed = false;
        _release.TrySetResult();
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Armed)
        {
            Blocked.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

