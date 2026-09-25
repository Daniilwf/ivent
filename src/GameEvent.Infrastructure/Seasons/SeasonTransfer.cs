using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Seasons;
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

/// <summary>What an import did: the season, how many events, the users it had to create, and the integrity check.</summary>
public sealed record ImportResult(Guid SeasonId, int Events, IReadOnlyList<string> CreatedUsers, IntegrityReport Integrity);

/// <summary>
/// Export and import of a season (D-32, D-105). The import writes outside the command queue: it loads a season no command
/// can reach yet into a local or test copy, in one transaction, and keeps it only if the integrity check passes.
/// </summary>
public static class SeasonTransfer
{
    public const int Format = 1;
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
        var playerUsers = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == seasonId).Select(p => p.UserId).ToListAsync(ct);
        var userIds = rows.Select(r => r.AuthorId).OfType<Guid>().Concat(playerUsers).Distinct().ToList();
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
        await using var stream = await entry.OpenAsync(ct);
        var archive = await JsonSerializer.DeserializeAsync<SeasonArchive>(stream, EngineJson.Options, ct)
            ?? throw new InvalidDataException($"{Entry} is empty.");
        return archive.Format == Format
            ? archive
            : throw new InvalidDataException($"Archive format {archive.Format}; this build reads format {Format}.");
    }

    /// <summary>
    /// Loads the archive's season: users matched by login (missing ones become deleted placeholders), missing games and
    /// categories added, the log written as it was (account ids mapped), the projection built command by command.
    /// Refused when the season is already in this database.
    /// </summary>
    public static async Task<ImportResult> ImportAsync(GameEventDbContext db, SeasonArchive archive, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(archive);
        if (await db.Seasons.AnyAsync(s => s.Id == archive.SeasonId, ct) || await db.Events.AnyAsync(e => e.SeasonId == archive.SeasonId, ct))
        {
            throw new InvalidOperationException($"Season {archive.SeasonId} is already in this database.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Users by login: the ids differ between databases
        var userMap = new Dictionary<Guid, Guid>();
        var created = new List<string>();
        foreach (var user in archive.Users)
        {
            var normalized = UserRecord.Normalize(user.Login);
            var local = await db.Users.SingleOrDefaultAsync(u => u.NormalizedLogin == normalized, ct);
            if (local is null)
            {
                local = new UserRecord
                {
                    Id = Guid.CreateVersion7(),
                    Login = user.Login,
                    NormalizedLogin = normalized,
                    Name = user.Name,
                    Role = user.Role,
                    PasswordHash = $"imported:{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}",
                    SecurityStamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                    MustChangePassword = true,
                    IsDeleted = true,
                    CreatedAt = now,
                };
                db.Users.Add(local);
                created.Add(user.Login);
            }

            userMap[user.Id] = local.Id;
        }

        foreach (var game in archive.Games)
        {
            if (!await db.Games.AnyAsync(g => g.Id == game.Id, ct))
            {
                db.Games.Add(new GameRecord { Id = game.Id, Title = game.Title, TagsJson = game.TagsJson, Hours = game.Hours, IsDeleted = game.IsDeleted });
            }
        }

        foreach (var category in archive.Categories)
        {
            if (!await db.Categories.AnyAsync(c => c.Name == category.Name, ct))
            {
                db.Categories.Add(new CategoryRecord { Name = category.Name, Weight = category.Weight });
            }
        }

        await db.SaveChangesAsync(ct);

        // The log: the players' accounts are this database's; everything else is the season's own. Older formats are
        // read up (D-49) and written in the current one.
        var records = new List<GameEventRecord>();
        var state = SeasonState.Empty;
        foreach (var command in archive.Events.OrderBy(e => e.Sequence).GroupBy(e => e.CommandId).OrderBy(g => g.Min(e => e.Sequence)))
        {
            var before = state;
            foreach (var exported in command.OrderBy(e => e.Sequence))
            {
                var decoded = EventCodec.Decode(new StoredEvent(exported.Type, exported.Version, exported.Data));
                // Account ids live in a player's arrival and in the player snapshots an undo keeps (D-104)
                decoded = decoded switch
                {
                    SeasonPlayerAdded added => added with { UserId = userMap.GetValueOrDefault(added.UserId, added.UserId) },
                    Engine.Undo.CommandUndone undone => undone with
                    {
                        Players = [.. undone.Players.Select(p => p with { UserId = userMap.GetValueOrDefault(p.UserId, p.UserId) })],
                    },
                    _ => decoded,
                };

                var stored = EventCodec.Encode(decoded);
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
                    AuthorId = exported.AuthorId is { } author ? userMap.GetValueOrDefault(author, author) : null,
                    OccurredAt = exported.OccurredAt,
                });
                state = SeasonEngine.Apply(state, decoded);
            }

            var first = command.First();
            await SeasonProjection.WriteAsync(db, before, state, first.OccurredAt, first.AuthorId is { } a ? userMap.GetValueOrDefault(a, a) : null, ct);
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

        var integrity = await SeasonIntegrity.CheckAsync(db, archive.SeasonId, ct)
            ?? throw new InvalidDataException("The archive has no season creation.");
        if (!integrity.IsIntact)
        {
            throw new InvalidDataException($"The imported season is not intact: {string.Join("; ", integrity.Differences)}");
        }

        await transaction.CommitAsync(ct);
        return new ImportResult(archive.SeasonId, records.Count, created, integrity);
    }
}
