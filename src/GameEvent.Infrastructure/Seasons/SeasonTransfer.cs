using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Undo;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Pool;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Seasons;

/// <summary>A user the season names: a player's account or an author of a command, matched by login on import.</summary>
public sealed record ExportedUser(Guid Id, string Login, string Name, Role Role);

/// <summary>An event of the log with its command and author, as stored (D-49: type, version, data).</summary>
public sealed record ExportedEvent(
    long Sequence,
    Guid CommandId,
    string CommandType,
    string CommandHash,
    string Type,
    int Version,
    string Data,
    Guid? AuthorId,
    DateTimeOffset OccurredAt,
    long? UndoneBySequence);

public sealed record ExportedGame(Guid Id, string Title, string TagsJson, decimal? Hours, bool IsDeleted);

public sealed record ExportedCategory(string Name, int Weight);

/// <summary>
/// The season archive (SPEC «Экспорт и импорт сезона», D-32, D-105): <c>season.json</c> in a zip — the log with commands
/// and authors, the users by login, the games and categories of the pool. The state is not stored: it is the fold of
/// the log, and the import checks that the projection it builds agrees.
/// </summary>
public sealed record SeasonArchive(
    int Format,
    Guid SeasonId,
    string Name,
    DateTimeOffset ExportedAt,
    IReadOnlyList<ExportedUser> Users,
    IReadOnlyList<ExportedGame> Games,
    IReadOnlyList<ExportedCategory> Categories,
    IReadOnlyList<ExportedEvent> Events);

/// <summary>An existing local account the archive's user was matched to by login, with both roles.</summary>
public sealed record MatchedUser(string Login, Role ArchiveRole, Role LocalRole);

/// <summary>How to import (D-105): accept a matched account whose role differs; add the pool as live games and categories.</summary>
public sealed record ImportOptions(bool AllowRoleMismatch = false, bool WithPool = false);

/// <summary>What an import did: the season, how many events, the accounts matched and created, and the integrity check.</summary>
public sealed record ImportResult(
    Guid SeasonId, int Events, IReadOnlyList<MatchedUser> MatchedUsers, IReadOnlyList<string> CreatedUsers, IntegrityReport Integrity);

/// <summary>
/// Export and import of a season (D-32, D-105). The import writes outside the command queue: it loads a season no command
/// can reach yet into a local or test copy, in one transaction, and keeps it only if the integrity check passes.
/// </summary>
public static class SeasonTransfer
{
    public const int Format = 1;

    /// <summary>The largest <c>season.json</c> an import reads: far above a season's log, far below a zip bomb.</summary>
    public const long MaxEntryBytes = 200L * 1024 * 1024;

    /// <summary>A placeholder account's password hash: no password matches it, and the login code knows the prefix.</summary>
    public const string PlaceholderHashPrefix = "imported:";

    private const string Entry = "season.json";

    public static async Task<SeasonArchive?> ExportAsync(GameEventDbContext db, Guid seasonId, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var season = await db.Seasons.AsNoTracking().SingleOrDefaultAsync(s => s.Id == seasonId, ct);
        if (season is null)
        {
            return null;
        }

        var rows = await db.Events.AsNoTracking().Where(e => e.SeasonId == seasonId).OrderBy(e => e.Sequence).ToListAsync(ct);
        var sequenceOf = rows.ToDictionary(r => r.Id, r => r.Sequence);

        // Every account the log names: authors, players' arrivals and the player snapshots undos keep
        var userIds = rows.Select(r => r.AuthorId).OfType<Guid>()
            .Concat(rows.SelectMany(r => AccountIds(EventCodec.Decode(new StoredEvent(r.Type, r.Version, r.Data)))))
            .Distinct()
            .ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).OrderBy(u => u.Login)
            .Select(u => new ExportedUser(u.Id, u.Login, u.Name, u.Role)).ToListAsync(ct);
        var games = await db.Games.AsNoTracking().OrderBy(g => g.Id)
            .Select(g => new ExportedGame(g.Id, g.Title, g.TagsJson, g.Hours, g.IsDeleted)).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().OrderBy(c => c.Name).Select(c => new ExportedCategory(c.Name, c.Weight)).ToListAsync(ct);

        return new SeasonArchive(
            Format,
            seasonId,
            season.Name,
            now,
            users,
            games,
            categories,
            [.. rows.Select(r => new ExportedEvent(
                r.Sequence, r.CommandId, r.CommandType, r.CommandHash, r.Type, r.Version, r.Data, r.AuthorId, r.OccurredAt,
                r.UndoneByEventId is { } undone ? sequenceOf[undone] : null))]);
    }

    public static async Task WriteZipAsync(SeasonArchive archive, Stream output, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await using var entry = await zip.CreateEntry(Entry, CompressionLevel.Optimal).OpenAsync(ct);
        await JsonSerializer.SerializeAsync(entry, archive, EngineJson.Options, ct);
    }

    public static async Task<SeasonArchive> ReadZipAsync(Stream input, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry(Entry) ?? throw new InvalidDataException($"The archive has no {Entry}.");
        if (entry.Length > MaxEntryBytes)
        {
            throw new InvalidDataException($"{Entry} unpacks to {entry.Length} bytes; the limit is {MaxEntryBytes}.");
        }

        await using var stream = await entry.OpenAsync(ct);
        SeasonArchive? archive;
        try
        {
            archive = await JsonSerializer.DeserializeAsync<SeasonArchive>(stream, EngineJson.Options, ct);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{Entry} is not a season archive: {e.Message}", e);
        }

        return archive is null ? throw new InvalidDataException($"{Entry} is empty.")
            : archive.Format == Format ? archive
            : throw new InvalidDataException($"Archive format {archive.Format}; this build reads format {Format}.");
    }

    /// <summary>
    /// Loads the archive's season (D-105): the archive is checked first; accounts matched by login (a different role is
    /// refused unless allowed), missing ones become deleted spectator placeholders; missing games are added as deleted
    /// (live, and categories, only <see cref="ImportOptions.WithPool"/>); the log is copied as stored, except the two
    /// events that name accounts; the projection is built command by command. Refused when the season is already here.
    /// </summary>
    public static Task<ImportResult> ImportAsync(
        GameEventDbContext db, SeasonArchive archive, DateTimeOffset now, ImportOptions? options = null, CancellationToken ct = default) =>
        ImportAsync(db, archive, now, options ?? new ImportOptions(), beforeCheck: null, ct);

    // `beforeCheck` lets a test spoil the projection to see the refusal (D-105).
    internal static async Task<ImportResult> ImportAsync(
        GameEventDbContext db, SeasonArchive archive, DateTimeOffset now, ImportOptions options, Func<GameEventDbContext, Task>? beforeCheck, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            return await ImportOnceAsync(db, archive, now, options, beforeCheck, ct);
        }
        catch
        {
            // The transaction is rolled back; what the context still tracks from it must not leak into a retry.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static async Task<ImportResult> ImportOnceAsync(
        GameEventDbContext db, SeasonArchive archive, DateTimeOffset now, ImportOptions options, Func<GameEventDbContext, Task>? beforeCheck, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(archive);
        var decoded = Validate(archive);
        if (await db.Seasons.AnyAsync(s => s.Id == archive.SeasonId, ct) || await db.Events.AnyAsync(e => e.SeasonId == archive.SeasonId, ct))
        {
            throw new InvalidOperationException($"Season {archive.SeasonId} is already in this database.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Accounts by login: the ids differ between databases
        var userMap = new Dictionary<Guid, Guid>();
        var matched = new List<MatchedUser>();
        var created = new List<string>();
        foreach (var user in archive.Users)
        {
            var normalized = UserRecord.Normalize(user.Login);
            var local = await db.Users.SingleOrDefaultAsync(u => u.NormalizedLogin == normalized, ct);
            if (local is null)
            {
                // A placeholder only names who played: the lowest role, deleted, no password matches it
                local = new UserRecord
                {
                    Id = Guid.CreateVersion7(),
                    Login = user.Login,
                    NormalizedLogin = normalized,
                    Name = user.Name,
                    Role = Role.Spectator,
                    PasswordHash = PlaceholderHashPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                    SecurityStamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                    MustChangePassword = true,
                    IsDeleted = true,
                    CreatedAt = now,
                };
                db.Users.Add(local);
                created.Add(user.Login);
            }
            else
            {
                matched.Add(new MatchedUser(user.Login, user.Role, local.Role));
                if (local.Role != user.Role && !options.AllowRoleMismatch)
                {
                    throw new InvalidDataException(
                        $"Account «{user.Login}» is {local.Role} here and {user.Role} in the archive; import again allowing the role mismatch if that is intended.");
                }
            }

            userMap[user.Id] = local.Id;
        }

        // The season's events only need its games to resolve; the live pool of this copy changes only when asked (D-19)
        foreach (var game in archive.Games)
        {
            if (!await db.Games.AnyAsync(g => g.Id == game.Id, ct))
            {
                db.Games.Add(new GameRecord { Id = game.Id, Title = game.Title, TagsJson = game.TagsJson, Hours = game.Hours, IsDeleted = game.IsDeleted || !options.WithPool });
            }
        }

        if (options.WithPool)
        {
            foreach (var category in archive.Categories)
            {
                if (!await db.Categories.AnyAsync(c => c.Name == category.Name, ct))
                {
                    db.Categories.Add(new CategoryRecord { Name = category.Name, Weight = category.Weight });
                }
            }
        }

        await db.SaveChangesAsync(ct);

        Guid MapAccount(Guid id) =>
            userMap.TryGetValue(id, out var local) ? local : throw new InvalidDataException($"The archive names account {id} but does not list it.");

        // The log is copied as stored (events are not rewritten); the two events naming accounts get this database's
        // account ids and are written in the current format (D-105).
        var records = new List<GameEventRecord>();
        var state = SeasonState.Empty;
        foreach (var command in archive.Events.Zip(decoded).GroupBy(x => x.First.CommandId).OrderBy(g => g.Min(x => x.First.Sequence)))
        {
            var before = state;
            foreach (var (exported, original) in command.OrderBy(x => x.First.Sequence))
            {
                var mapped = original switch
                {
                    SeasonPlayerAdded added => added with { UserId = MapAccount(added.UserId) },
                    CommandUndone undone => undone with { Players = [.. undone.Players.Select(p => p with { UserId = MapAccount(p.UserId) })] },
                    _ => original,
                };
                var stored = ReferenceEquals(mapped, original) ? new StoredEvent(exported.Type, exported.Version, exported.Data) : EventCodec.Encode(mapped);
                records.Add(new GameEventRecord
                {
                    SeasonId = archive.SeasonId,
                    Sequence = exported.Sequence,
                    CommandId = exported.CommandId,
                    CommandType = exported.CommandType,
                    CommandHash = exported.CommandHash,
                    Type = stored.Type,
                    Version = stored.Version,
                    Data = stored.Data,
                    AuthorId = exported.AuthorId is { } author ? MapAccount(author) : null,
                    OccurredAt = exported.OccurredAt,
                });
                state = SeasonEngine.Apply(state, mapped);
            }

            var first = command.OrderBy(x => x.First.Sequence).First().First;
            await SeasonProjection.WriteAsync(db, before, state, first.OccurredAt, first.AuthorId is { } a ? MapAccount(a) : null, ct);
            await db.SaveChangesAsync(ct);
        }

        db.Events.AddRange(records);
        await db.SaveChangesAsync(ct);

        // Undone commands keep their mark, now pointing at this database's event ids
        var bySequence = records.ToDictionary(r => r.Sequence);
        foreach (var exported in archive.Events.Where(e => e.UndoneBySequence is not null))
        {
            bySequence[exported.Sequence].UndoneByEventId = bySequence[exported.UndoneBySequence!.Value].Id;
        }

        await db.SaveChangesAsync(ct);
        if (beforeCheck is not null)
        {
            await beforeCheck(db);
        }

        var integrity = await SeasonIntegrity.CheckAsync(db, archive.SeasonId, ct)
            ?? throw new InvalidDataException("The archive has no season creation.");
        if (!integrity.IsIntact)
        {
            throw new InvalidDataException($"The imported season is not intact: {string.Join("; ", integrity.Differences)}");
        }

        await transaction.CommitAsync(ct);
        return new ImportResult(archive.SeasonId, records.Count, matched, created, integrity);
    }

    /// <summary>
    /// One consistent season, checked before anything is written (D-105): events numbered 1..N, the first one this
    /// season's creation, every undo mark and account resolvable, logins unique, the pool well formed. Returns the
    /// decoded events.
    /// </summary>
    private static List<IGameEvent> Validate(SeasonArchive archive)
    {
        if (archive.Events.Count == 0)
        {
            throw new InvalidDataException("The archive has no events.");
        }

        var sequences = archive.Events.Select(e => e.Sequence).ToList();
        if (!sequences.SequenceEqual(Enumerable.Range(1, sequences.Count).Select(i => (long)i)))
        {
            throw new InvalidDataException("Events must be numbered 1..N in order, without gaps or repeats.");
        }

        List<IGameEvent> decoded;
        try
        {
            decoded = [.. archive.Events.Select(e => EventCodec.Decode(new StoredEvent(e.Type, e.Version, e.Data)))];
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"An event cannot be read by this build: {e.Message}", e);
        }

        if (decoded[0] is not SeasonCreated created || created.SeasonId != archive.SeasonId)
        {
            throw new InvalidDataException("The first event must create the archive's season.");
        }

        if (archive.Events.Any(e => e.UndoneBySequence is { } by && (by < 1 || by > sequences.Count)))
        {
            throw new InvalidDataException("An undo mark points at an event the archive does not have.");
        }

        if (archive.Users.Select(u => UserRecord.Normalize(u.Login)).Distinct().Count() != archive.Users.Count
            || archive.Users.Select(u => u.Id).Distinct().Count() != archive.Users.Count
            || archive.Users.Any(u => string.IsNullOrWhiteSpace(u.Login) || string.IsNullOrWhiteSpace(u.Name)))
        {
            throw new InvalidDataException("Accounts must have unique ids and logins and non-empty names.");
        }

        var listed = archive.Users.Select(u => u.Id).ToHashSet();
        if (archive.Events.Select(e => e.AuthorId).OfType<Guid>().Concat(decoded.SelectMany(AccountIds)).Any(id => !listed.Contains(id)))
        {
            throw new InvalidDataException("The log names an account the archive does not list.");
        }

        foreach (var game in archive.Games)
        {
            if (string.IsNullOrWhiteSpace(game.Title) || game.Hours is <= 0 || !TagsAreValid(game.TagsJson))
            {
                throw new InvalidDataException($"Game {game.Id} is malformed: a title, positive hours or none, tags as a list of strings.");
            }
        }

        if (archive.Games.Select(g => g.Id).Distinct().Count() != archive.Games.Count
            || archive.Categories.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Weight < 0)
            || archive.Categories.Select(c => c.Name).Distinct().Count() != archive.Categories.Count)
        {
            throw new InvalidDataException("Games and categories must be unique; categories need a name and a weight of 0 or more.");
        }

        return decoded;
    }

    private static bool TagsAreValid(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, EngineJson.Options) is { } tags && tags.All(t => !string.IsNullOrWhiteSpace(t));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The events that carry account ids: a player's arrival and the player snapshots an undo keeps (D-104).
    private static IEnumerable<Guid> AccountIds(IGameEvent e) =>
        e switch
        {
            SeasonPlayerAdded added => [added.UserId],
            CommandUndone undone => undone.Players.Select(p => p.UserId),
            _ => [],
        };
}
