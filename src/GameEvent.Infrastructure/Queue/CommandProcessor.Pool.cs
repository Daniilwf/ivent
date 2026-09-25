using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Pool;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Queue;

/// <summary>
/// Pool commands (D-119): one at a time in the global log, so two players adding the same game at once cannot both pass
/// the duplicate check. The pool tables hold the current cards; the log is the history of who added and changed what.
/// </summary>
public sealed partial class CommandProcessor
{
    private async Task<(IReadOnlyList<IGameEvent> Events, Action? Apply, string? Secret, Rejection? Rejection)> DecidePoolAsync(
        GameEventDbContext db, IGlobalCommand command, DateTimeOffset now, CancellationToken ct)
    {
        static (IReadOnlyList<IGameEvent>, Action?, string?, Rejection?) Reject(string code, string detail) => ([], null, null, new Rejection(code, detail));

        switch (command)
        {
            case AddGame add:
                {
                    var (card, problem) = PoolRules.Normalize(add.Card);
                    if (card is null)
                    {
                        return Reject(PoolRules.CardInvalid, problem!);
                    }

                    if (await TitleProblemAsync(db, card.Title, null, add.Force, ct) is { } titleProblem)
                    {
                        return ([], null, null, titleProblem);
                    }

                    if (await CoverProblemAsync(db, card.CoverFileId, ct) is { } coverProblem)
                    {
                        return ([], null, null, coverProblem);
                    }

                    var gameId = ids.NewId();
                    var record = new GameRecord { Id = gameId, Title = card.Title, TagsJson = "[]", AuthorId = add.AuthorId, CreatedAt = now };
                    Fill(record, card);
                    return ([new GameAdded(gameId, card, add.AuthorId)], () => db.Games.Add(record), null, null);
                }

            case ChangeGame change:
                {
                    if (await db.Games.SingleOrDefaultAsync(g => g.Id == change.GameId, ct) is not { } record)
                    {
                        return Reject(PoolRules.Unknown, $"Game {change.GameId} is not in the pool.");
                    }

                    var (card, problem) = PoolRules.Normalize(change.Card);
                    if (card is null)
                    {
                        return Reject(PoolRules.CardInvalid, problem!);
                    }

                    if (card == Card(record))
                    {
                        return Reject(PoolRules.NothingToChange, "The card is already this one.");
                    }

                    if (!PoolRules.IsSame(card.Title, record.Title) && await TitleProblemAsync(db, card.Title, record.Id, change.Force, ct) is { } titleProblem)
                    {
                        return ([], null, null, titleProblem);
                    }

                    if (card.CoverFileId != record.CoverFileId && await CoverProblemAsync(db, card.CoverFileId, ct) is { } coverProblem)
                    {
                        return ([], null, null, coverProblem);
                    }

                    return ([new GameChanged(record.Id, card)], () => Fill(record, card), null, null);
                }

            case DeleteGame delete:
                {
                    if (await db.Games.SingleOrDefaultAsync(g => g.Id == delete.GameId, ct) is not { } record)
                    {
                        return Reject(PoolRules.Unknown, $"Game {delete.GameId} is not in the pool.");
                    }

                    return record.IsDeleted
                        ? Reject(PoolRules.Deleted, "The game is already deleted.")
                        : ([new GameDeleted(record.Id)], () => record.IsDeleted = true, null, null);
                }

            case RestoreGame restore:
                {
                    if (await db.Games.SingleOrDefaultAsync(g => g.Id == restore.GameId, ct) is not { } record)
                    {
                        return Reject(PoolRules.Unknown, $"Game {restore.GameId} is not in the pool.");
                    }

                    if (!record.IsDeleted)
                    {
                        return Reject(PoolRules.NotDeleted, "The game is not deleted.");
                    }

                    // Back in the pool only if no game took its title meanwhile
                    var titles = await db.Games.AsNoTracking().Where(g => g.Id != record.Id && !g.IsDeleted).Select(g => g.Title).ToListAsync(ct);
                    return titles.Any(t => PoolRules.IsSame(t, record.Title))
                        ? Reject(PoolRules.Duplicate, $"The pool already has «{record.Title}».")
                        : ([new GameRestored(record.Id)], () => record.IsDeleted = false, null, null);
                }

            case SetCategory set:
                {
                    if (PoolRules.CheckCategory(set.Name, set.Weight) is { } invalid)
                    {
                        return ([], null, null, invalid);
                    }

                    var name = set.Name.Trim();
                    var existing = await db.Categories.SingleOrDefaultAsync(c => c.Name == name, ct);
                    if (existing?.Weight == set.Weight)
                    {
                        return Reject(PoolRules.NothingToChange, "The category already has this weight.");
                    }

                    return ([new CategorySet(name, set.Weight)], () =>
                    {
                        if (existing is null)
                        {
                            db.Categories.Add(new CategoryRecord { Name = name, Weight = set.Weight });
                        }
                        else
                        {
                            existing.Weight = set.Weight;
                        }
                    }, null, null);
                }

            case RemoveCategory remove:
                {
                    var name = remove.Name?.Trim() ?? "";
                    return await db.Categories.SingleOrDefaultAsync(c => c.Name == name, ct) is not { } category
                        ? Reject(PoolRules.CategoryUnknown, $"The wheel has no category «{name}».")
                        : ([new CategoryRemoved(category.Name)], () => db.Categories.Remove(category), null, null);
                }

            default:
                throw new InvalidOperationException($"Unknown pool command {command.GetType().Name}.");
        }
    }

    // The same title never twice among the games in the pool; an alike one only when the author confirmed it (SPEC «Дубли»)
    private static async Task<Rejection?> TitleProblemAsync(GameEventDbContext db, string title, Guid? self, bool force, CancellationToken ct)
    {
        var titles = await db.Games.AsNoTracking().Where(g => !g.IsDeleted && g.Id != self).Select(g => g.Title).ToListAsync(ct);
        if (titles.FirstOrDefault(t => PoolRules.IsSame(t, title)) is { } same)
        {
            return new Rejection(PoolRules.Duplicate, $"The pool already has «{same}».");
        }

        return !force && titles.Where(t => PoolRules.IsAlike(t, title)).ToList() is { Count: > 0 } alike
            ? new Rejection(PoolRules.Similar, $"The pool has similar titles: {string.Join(", ", alike.Select(t => $"«{t}»"))}.")
            : null;
    }

    private static async Task<Rejection?> CoverProblemAsync(GameEventDbContext db, Guid? coverFileId, CancellationToken ct) =>
        coverFileId is { } fileId && !await db.Files.AnyAsync(f => f.Id == fileId && !f.IsDeleted, ct)
            ? new Rejection(PoolRules.CoverUnknown, $"File {fileId} is not stored.")
            : null;

    private static void Fill(GameRecord record, GameCard card)
    {
        record.Title = card.Title;
        record.TagsJson = PoolReader.TagsToJson(card.Tags);
        record.Hours = card.Hours;
        record.Year = card.Year;
        record.SteamAppId = card.SteamAppId;
        record.CoverFileId = card.CoverFileId;
        record.Note = card.Note;
        record.IsCoop = card.IsCoop;
    }

    internal static GameCard Card(GameRecord record) =>
        new(record.Title, PoolReader.Tags(record.TagsJson), record.Hours, record.Year, record.SteamAppId, record.CoverFileId, record.Note, record.IsCoop);
}
