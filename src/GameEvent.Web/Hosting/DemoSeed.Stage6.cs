using System.Security.Cryptography;
using GameEvent.Engine.Comments;
using GameEvent.Engine.Content;
using GameEvent.Engine.Files;
using GameEvent.Engine.Gallery;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Reactions;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Files;
using GameEvent.Web.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Hosting;

public static partial class DemoSeed
{
    private sealed class Stage6Demo(
        IDbContextFactory<GameEventDbContext> factory,
        CommandBus bus,
        SeasonStateReader reader,
        Guid admin,
        CancellationToken ct)
    {
        private static readonly Guid s_archiveId = Guid.Parse("de300000-0000-0000-0000-000000000002");

        public async Task PlayAsync(string contentRoot, IServiceProvider services)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var initial = await reader.ReadAsync(db, SeasonId, ct);
            var features = initial.Rules.Features with { Reactions = true, Comments = true, Gallery = true, SeasonStatistics = true };
            await SendAsync(SeasonId, new ChangeRuleset(initial.Rules with { Features = features }), admin);

            var actors = await db.Users.AsNoTracking()
                .Where(user => user.Login == "player01" || user.Login == "player02" || user.Login == "zritel")
                .ToDictionaryAsync(user => user.Login, user => user.Id, ct);
            var player = actors["player01"];
            var spectator = actors["zritel"];
            await AddDiscussionsAsync(db, player, spectator);
            await PublishMomentAsync(contentRoot, services, db, player, spectator);
            await ArchiveSeasonAsync(contentRoot, player, actors["player02"]);
        }

        private async Task AddDiscussionsAsync(GameEventDbContext db, Guid player, Guid spectator)
        {
            var recentProof = await db.Events.AsNoTracking()
                .Where(row => row.SeasonId == SeasonId && row.Type == "irl-proof-submitted" && row.UndoneByEventId == null)
                .OrderByDescending(row => row.Sequence).Select(row => row.CommandId).FirstAsync(ct);
            var review = await (
                from entry in db.Reviews.AsNoTracking()
                join proof in db.Proofs.AsNoTracking() on entry.RunId equals proof.RunId
                join run in db.Runs.AsNoTracking() on entry.RunId equals run.Id
                where entry.SeasonId == SeasonId && entry.Text != null && proof.Status == ProofStatus.Approved
                orderby run.CompletedAt descending, entry.RunId
                select entry.RunId).FirstAsync(ct);

            await SendAsync(SeasonId, new SetFeedReaction(recentProof, player, "🔥", Selected: true), player);
            await SendAsync(SeasonId, new SetFeedReaction(recentProof, spectator, "🔥", Selected: true), spectator);
            var feedTarget = new CommentTarget(CommentTargetKind.FeedEntry, recentProof);
            await SendAsync(SeasonId, new PostComment(Guid.CreateVersion7(), feedTarget, spectator,
                "Караоке в игровом сезоне — вот это поворот."), spectator);
            await SendAsync(SeasonId, new PostComment(Guid.CreateVersion7(), feedTarget, player,
                "Теперь тоже выбираю песню на случай такого задания."), player);

            await SendAsync(SeasonId, new SetReviewReaction(review, spectator, "👏", Selected: true), spectator);
            var reviewTarget = new CommentTarget(CommentTargetKind.Review, review);
            await SendAsync(SeasonId, new PostComment(Guid.CreateVersion7(), reviewTarget, spectator,
                "Спасибо за отзыв, добавлю игру в список."), spectator);
        }

        private async Task PublishMomentAsync(string contentRoot, IServiceProvider services, GameEventDbContext db, Guid player, Guid spectator)
        {
            var files = new FileServices(db, services.GetRequiredService<FileStorage>(), services.GetRequiredService<FileLimits>(),
                services.GetRequiredService<IIdGenerator>(), services.GetRequiredService<IClock>(), bus);
            const string SourceName = "map.demo.background.png";
            await using var source = File.OpenRead(Path.Combine(contentRoot, "content", SourceName));
            var fingerprint = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
            source.Position = 0;
            var file = new FormFile(source, 0, source.Length, "file", SourceName);
            var (picture, _) = await FileEndpoints.ProcessPictureAsync(file, FileKind.GalleryImage, files.Limits, ct);
            if (picture is null)
            {
                throw new InvalidOperationException("The demo gallery picture could not be encoded.");
            }

            var moment = new PublishGalleryMoment(Guid.CreateVersion7(), player, "Иллюстрация демо-карты перед стартом", fingerprint);
            var outcome = await GalleryEndpoints.StoreAndPublishAsync(Guid.CreateVersion7(), SeasonId, moment, picture, files);
            RequireAccepted(moment, outcome);
            await SendAsync(SeasonId, new SetGalleryReaction(moment.ItemId, spectator, "❤️", Selected: true), spectator);
        }

        private async Task ArchiveSeasonAsync(string contentRoot, Guid player, Guid opponent)
        {
            var seat = Guid.CreateVersion7();
            var opponentSeat = Guid.CreateVersion7();
            var rules = RulesetJson.Default();
            await SendAsync(s_archiveId, new CreateSeason(s_archiveId, "Демо-сезон: первый круг", rules), admin);
            await SendAsync(s_archiveId, new AddSeasonPlayer(seat, player, s_names[0]), admin);
            await SendAsync(s_archiveId, new AddSeasonPlayer(opponentSeat, opponent, s_names[1]), admin);
            await SendAsync(s_archiveId, new ChangeSeasonStatus(SeasonStatus.Active), admin);

            await SendAsync(s_archiveId, new RollGame(seat), player);
            var started = (await SendAsync(s_archiveId, new StartRun(seat), player)).Select(row => row.Event).OfType<RunStarted>().Single();
            await SendAsync(s_archiveId, new CompleteRun(seat, Difficulty.Normal,
                EstimatedHours: started.Snapshot.Hours is null ? 5 : null,
                HoursSource: started.Snapshot.Hours is null ? "Оценка демо-прохождения" : null,
                Review: new RunReview(8, "Хорошая игра для первого круга.")), player);

            // Keep the original archived run and result independent of the attack rolls.
            var features = rules.Features with { Items = true, Achievements = true };
            await SendAsync(s_archiveId, new ChangeRuleset(rules with { Features = features }), admin);
            var pack = ContentJson.Parse<ContentPack>(await File.ReadAllTextAsync(
                Path.Combine(contentRoot, "content", "stage6.demo.achievements.json"), ct));
            var bird = ContentJson.Parse<ObjectDefinition>(await File.ReadAllTextAsync(
                Path.Combine(contentRoot, "content", "items", "bird-thief.json"), ct));
            await SendAsync(s_archiveId, new PublishContent(pack with { Objects = [.. pack.Objects, bird] }, "Демо сезонных достижений"), admin);

            var requiredHits = pack.Objects.Single(definition => definition.Id == "unbreakable").Effect?.Condition?.Gte
                ?? throw new InvalidOperationException("The demo achievement needs an attack threshold.");
            await SendAsync(s_archiveId, new AdjustPlayer(seat, "Монетки для демонстрации достижения", CoinsDelta: 20), admin);
            for (var attack = 0; attack < requiredHits; attack++)
            {
                var gift = await SendAsync(s_archiveId, new AdjustInventory(opponentSeat, bird.Id, null, "Демо Птичкерса"), admin);
                var item = gift.Select(row => row.Event).OfType<ObjectGiven>().Single();
                await SendAsync(s_archiveId, new UseItem(opponentSeat, item.Item.InstanceId, seat), opponent);
            }

            await SendAsync(s_archiveId, new ChangeSeasonStatus(SeasonStatus.Closing), admin);
            await SendAsync(s_archiveId, new ApproveProof(started.RunId, Comment: "Подтверждено демо-прохождение"), admin);
            await SendAsync(s_archiveId, new ChangeSeasonStatus(SeasonStatus.Finished), admin);
            await SendAsync(s_archiveId, new ChangeSeasonStatus(SeasonStatus.Archived), admin);
        }

        private async Task<IReadOnlyList<LoggedEvent>> SendAsync(Guid seasonId, ICommand command, Guid author)
        {
            var outcome = await bus.SendAsync(new CommandEnvelope(Guid.CreateVersion7(), seasonId, command, author), ct);
            RequireAccepted(command, outcome);
            return outcome.Events;
        }

        private static void RequireAccepted(ICommand command, CommandOutcome outcome)
        {
            if (!outcome.IsAccepted)
            {
                throw new InvalidOperationException($"Stage 6 demo command {command.GetType().Name}: {outcome.Rejection?.Code}: {outcome.Rejection?.Detail}");
            }
        }
    }
}
