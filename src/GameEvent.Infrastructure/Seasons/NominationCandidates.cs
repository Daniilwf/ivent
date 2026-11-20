using GameEvent.Engine.Files;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Seasons;

/// <summary>A currently public candidate. Its content is resolved at read time and never copied into a ballot.</summary>
public sealed record NominationCandidate(Guid Id, string Label, string? Detail = null, Guid? GameId = null, bool HasThumbnail = false);

public static class NominationCandidates
{
    public static async Task<IReadOnlyList<NominationCandidate>> ListAsync(
        GameEventDbContext db, Guid seasonId, VotedNomination category, bool galleryEnabled, IReadOnlyCollection<Guid>? candidateIds,
        CancellationToken ct)
    {
        if (candidateIds is { Count: 0 })
        {
            return [];
        }

        var credited = db.Runs.AsNoTracking().Where(run => run.SeasonId == seasonId && run.Status == RunStatus.Completed
            && db.Proofs.Any(proof => proof.RunId == run.Id && proof.SeasonId == seasonId && proof.Status == ProofStatus.Approved));
        var games = db.Games.AsNoTracking().Where(game => !game.IsDeleted);

        switch (category)
        {
            case VotedNomination.BestReview:
                var reviews = db.Reviews.AsNoTracking().Where(review => review.SeasonId == seasonId
                    && review.Text != null && review.Text.Trim() != "");
                if (candidateIds is not null)
                {
                    reviews = reviews.Where(review => candidateIds.Contains(review.RunId));
                }

                return await (
                    from review in reviews
                    join run in credited on review.RunId equals run.Id
                    join game in games on run.GameId equals game.Id
                    orderby game.Title, review.RunId
                    select new NominationCandidate(review.RunId, game.Title, review.Text, game.Id, false)).ToListAsync(ct);

            case VotedNomination.GameOfSeason:
                if (candidateIds is not null)
                {
                    games = games.Where(game => candidateIds.Contains(game.Id));
                }

                return await games.Where(game => credited.Any(run => run.GameId == game.Id))
                    .OrderBy(game => game.Title).ThenBy(game => game.Id)
                    .Select(game => new NominationCandidate(game.Id, game.Title, null, game.Id, false)).ToListAsync(ct);

            case VotedNomination.FunniestMoment when galleryEnabled:
                var moments = db.GalleryMoments.AsNoTracking().Where(item => item.SeasonId == seasonId && !item.Hidden && !item.ConsentRevoked);
                if (candidateIds is not null)
                {
                    moments = moments.Where(item => candidateIds.Contains(item.ItemId));
                }

                return await (
                    from item in moments
                    let hasThumbnail = db.GalleryMediaBindings.Any(binding =>
                        binding.SeasonId == item.SeasonId && binding.ItemId == item.ItemId && binding.FileId == item.FileId
                        && db.Files.Any(file => file.Id == binding.FileId && file.Kind == FileKind.GalleryImage
                            && !file.IsDeleted && file.OwnerId == item.UserId))
                    orderby item.CreatedAt descending, item.ItemId descending
                    select new NominationCandidate(item.ItemId, item.Caption, null, null, hasThumbnail)).ToListAsync(ct);

            default:
                return [];
        }
    }
}
