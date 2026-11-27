using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.Database;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Seasons;

/// <summary>The rules in force of a season for a read, and the answer of a read whose mechanic is off.</summary>
internal static class SeasonRules
{
    /// <summary>The season's rules, or null when there is no such season.</summary>
    public static async Task<Ruleset?> OfAsync(GameEventDbContext db, Guid seasonId, CancellationToken ct)
    {
        if (seasonId == Guid.Empty)
        {
            return null;
        }

        var json = await db.Seasons.AsNoTracking().Where(s => s.Id == seasonId).Select(s => s.RulesetJson).SingleOrDefaultAsync(ct);
        return json is null ? null : RulesetJson.Parse(json);
    }

    /// <summary>
    /// A mechanic switched off is not shown at all (CLAUDE.md «Выключенная механика не видна в интерфейсе»): its read answers
    /// 409 <c>feature.disabled</c>, as its commands do.
    /// </summary>
    public static ProblemHttpResult FeatureOff(string detail) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The mechanic is off.",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = RejectionCodes.FeatureDisabled });
}
