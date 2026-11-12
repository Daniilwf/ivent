using System.Security.Claims;
using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Http.HttpResults;

using ActionResult = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Ok<GameEvent.Web.Seasons.CommandResponse>,
    Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult,
    Microsoft.AspNetCore.Http.HttpResults.ValidationProblem,
    Microsoft.AspNetCore.Http.HttpResults.NotFound,
    Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>;

namespace GameEvent.Web.Economy;

/// <summary>The admin gives a player an object by its id or takes one by its instance, with a comment (D-407).</summary>
public sealed record AdjustInventoryRequest(Guid CommandId, string? Comment, string? GiveObjectId = null, Guid? RemoveInstanceId = null);

/// <summary>
/// The economy in the admin's hands (stage 4, D-442): the manual correction of a player's inventory. The season's content
/// is read and published by the content editor's API alone (<see cref="Content.AdminContentEndpoints"/>, D-446). Admin
/// only; every change is a command of the season log.
/// </summary>
public static class AdminEconomyEndpoints
{
    public static void MapAdminEconomy(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var admin = api.MapGroup("/admin/seasons/{seasonId:guid}").WithTags("Admin").RequireAuthorization(Policies.Admin);
        admin.MapPost("/players/{playerId:guid}/inventory", (Guid seasonId, Guid playerId, AdjustInventoryRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                AdjustAsync(seasonId, playerId, request, user, db, bus, ct))
            .WithActionErrors();
    }

    private static async Task<ActionResult> AdjustAsync(
        Guid seasonId, Guid playerId, AdjustInventoryRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (await SeasonEndpoints.PrecheckAsync(seasonId, request.CommandId, db, ct) is { } refused)
        {
            return refused;
        }

        if (request.Comment is null)
        {
            return Invalid(new() { ["comment"] = ["A comment is required."] });
        }

        if (request.GiveObjectId is { } give && (give.Length is 0 or > ContentValidator.MaxIdLength))
        {
            return Invalid(new() { ["giveObjectId"] = [$"An object id is 1–{ContentValidator.MaxIdLength} characters."] });
        }

        if (request.RemoveInstanceId == Guid.Empty)
        {
            return Invalid(new() { ["removeInstanceId"] = ["An instance id is required."] });
        }

        return await SeasonEndpoints.SendAsync(
            seasonId, request.CommandId, new AdjustInventory(playerId, request.GiveObjectId, request.RemoveInstanceId, request.Comment), user.UserId(), bus, ct);
    }

    private static ValidationProblem Invalid(Dictionary<string, string[]> errors) => TypedResults.ValidationProblem(errors);
}
