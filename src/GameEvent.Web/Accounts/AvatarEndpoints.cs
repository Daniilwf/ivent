using System.Security.Claims;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Files;
using GameEvent.Web.Hosting;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Accounts;

/// <summary>A new avatar: a stored file (uploaded or downloaded by link) or none to remove it (D-117).</summary>
public sealed record AvatarRequest(Guid CommandId, Guid? FileId);

/// <summary>The avatar after the change; <c>duplicate</c> — the same request was already handled.</summary>
public sealed record AvatarResponse(bool Duplicate, FileLinkView? Avatar);

/// <summary>
/// Avatars (SPEC «Аватарки», D-117): everyone sets their own from their own uploads; the admin sets anyone's from that
/// player's upload or his own. The file itself comes from <c>POST /api/files</c> or <c>POST /api/files/from-url</c>.
/// </summary>
public static class AvatarEndpoints
{
    public const string NotYours = "account.avatarNotYours";

    public static void MapAvatars(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapPut("/auth/me/avatar", (AvatarRequest request, ClaimsPrincipal principal, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                principal.UserId() is { } me
                    ? SetAsync(request, me, [me], principal, db, bus, ct)
                    : Task.FromResult<IResult>(TypedResults.Forbid()))
            .WithTags("Auth")
            .RequireAuthorization()
            .WithAvatarErrors();

        api.MapPut("/admin/accounts/{userId:guid}/avatar", (Guid userId, AvatarRequest request, ClaimsPrincipal principal, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                SetAsync(request, userId, [userId, principal.UserId() ?? Guid.Empty], principal, db, bus, ct))
            .WithTags("Admin")
            .RequireAuthorization(Policies.Admin)
            .WithAvatarErrors();
    }

    private static async Task<IResult> SetAsync(
        AvatarRequest request, Guid userId, Guid[] owners, ClaimsPrincipal principal, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        if (request.CommandId == Guid.Empty)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["commandId"] = ["A command id is required."] });
        }

        // Only a picture the account's owner (or the admin setting it) uploaded: nobody wears someone else's upload
        if (request.FileId is { } fileId && !await db.Files.AsNoTracking().AnyAsync(f => f.Id == fileId && !f.IsDeleted && owners.Contains(f.OwnerId), ct))
        {
            return Rejected(NotYours, "The avatar is one of your own uploads.");
        }

        var outcome = await bus.SendAsync(new CommandEnvelope(request.CommandId, Guid.Empty, new SetAvatar(userId, request.FileId), principal.UserId()), ct);
        if (!outcome.IsAccepted)
        {
            return outcome.Rejection!.Code == AccountRules.Unknown
                ? TypedResults.NotFound()
                : Rejected(outcome.Rejection.Code, outcome.Rejection.Detail);
        }

        var avatar = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.AvatarFileId).SingleAsync(ct);
        return TypedResults.Ok(new AvatarResponse(outcome.IsDuplicate, avatar is { } id ? FileLinkView.Of(id) : null));
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Rejected(string code, string detail) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The command was rejected.",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static RouteHandlerBuilder WithAvatarErrors(this RouteHandlerBuilder builder) =>
        builder
            .Produces<AvatarResponse>()
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
}
