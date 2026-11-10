using System.Security.Claims;
using System.Text.Json;
using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

using ActionResult = Microsoft.AspNetCore.Http.HttpResults.Results<
    Microsoft.AspNetCore.Http.HttpResults.Ok<GameEvent.Web.Seasons.CommandResponse>,
    Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult,
    Microsoft.AspNetCore.Http.HttpResults.ValidationProblem,
    Microsoft.AspNetCore.Http.HttpResults.NotFound,
    Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>;

namespace GameEvent.Web.Economy;

/// <summary>
/// The admin loads a content pack into the season (D-400, D-442): <c>pack</c> — the format of CONTENT.md
/// (<c>{"objects": […], "wheels": […]}</c>, the files of <c>/content/items</c> and <c>/content/wheels</c> together), with a
/// comment for the public log. Every problem of the pack is answered at once, keyed by its JSON path.
/// </summary>
public sealed record PublishContentRequest(Guid CommandId, JsonElement Pack, string? Comment);

/// <summary>The admin gives a player an object by its id or takes one by its instance, with a comment (D-407).</summary>
public sealed record AdjustInventoryRequest(Guid CommandId, string? Comment, string? GiveObjectId = null, Guid? RemoveInstanceId = null);

/// <summary>The season's content in force: its version (0 — nothing loaded), every definition ever published and the wheels.</summary>
public sealed record ContentView(int Version, IReadOnlyList<ContentObjectView> Objects, IReadOnlyList<ContentWheelView> Wheels);

/// <summary>A definition: <c>deleted</c> once a later pack no longer has it (objects held keep working, invariant 11).</summary>
public sealed record ContentObjectView(string Id, ObjectKind Kind, string Name, Rarity? Rarity, int? Price, bool Deleted);

public sealed record ContentWheelView(string Id, string Name, int Entries);

/// <summary>
/// The economy in the admin's hands (stage 4, D-442): the season's content — read it, load a new pack — and the manual
/// correction of a player's inventory. Admin only; every change is a command of the season log.
/// </summary>
public static class AdminEconomyEndpoints
{
    /// <summary>The largest pack a request carries, in characters of its JSON; the repository's whole content is far less.</summary>
    public const int MaxPackLength = 1_000_000;

    public static void MapAdminEconomy(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var admin = api.MapGroup("/admin/seasons/{seasonId:guid}").WithTags("Admin").RequireAuthorization(Policies.Admin);
        admin.MapGet("/content", GetContentAsync)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        admin.MapPost("/content", PublishAsync).WithActionErrors();
        admin.MapPost("/players/{playerId:guid}/inventory", (Guid seasonId, Guid playerId, AdjustInventoryRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                AdjustAsync(seasonId, playerId, request, user, db, bus, ct))
            .WithActionErrors();
    }

    private static async Task<Results<Ok<ContentView>, NotFound>> GetContentAsync(Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        if (seasonId == Guid.Empty || !await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
        var catalog = state.Catalog;
        return TypedResults.Ok(new ContentView(
            catalog.Version,
            [.. catalog.Objects.Values.Select(e => new ContentObjectView(e.Definition.Id, e.Definition.Kind, e.Definition.Name, e.Definition.Rarity, e.Definition.Price, e.Deleted))],
            [.. catalog.Wheels.Values.Select(w => new ContentWheelView(w.Id, w.Name, w.Entries.Count))]));
    }

    private static async Task<ActionResult> PublishAsync(
        Guid seasonId, PublishContentRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (await SeasonEndpoints.PrecheckAsync(seasonId, request.CommandId, db, ct) is { } refused)
        {
            return refused;
        }

        if (request.Comment is null)
        {
            return Invalid(new() { ["comment"] = ["A comment is required."] });
        }

        if (request.Pack.ValueKind != JsonValueKind.Object)
        {
            return Invalid(new() { ["pack"] = ["A pack is a JSON object with «objects» and «wheels»."] });
        }

        var text = request.Pack.GetRawText();
        if (text.Length > MaxPackLength)
        {
            return Invalid(new() { ["pack"] = [$"A pack is at most {MaxPackLength} characters of JSON."] });
        }

        ContentPack pack;
        try
        {
            pack = ContentJson.Parse<ContentPack>(text);
        }
        catch (JsonException e)
        {
            return Invalid(new() { ["pack"] = [e.Message] });
        }

        // All the problems at once, each at its path (the engine checks the same again in the queue)
        var errors = ContentPublishing.Check(pack);
        if (errors.Count > 0)
        {
            return Invalid(errors.GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()));
        }

        return await SeasonEndpoints.SendAsync(seasonId, request.CommandId, new PublishContent(pack, request.Comment), user.UserId(), bus, ct);
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
