using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Finish;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Rulesets;

/// <summary>The rules in force and their history: who changed what and when (C3). Numbers on the rules page are real.</summary>
public sealed record RulesView(int Version, Ruleset Ruleset, IReadOnlyList<RulesVersionView> History);

/// <summary>One version of the rules: when, by whom (null for the season's creation by the system), and what changed.</summary>
public sealed record RulesVersionView(int Version, DateTimeOffset At, Guid? AuthorId, IReadOnlyList<RulesetChange> Changes);

/// <summary>
/// The new version of the rules and what the admin should know about it: <c>finish.bonusesKept</c> — the finish bonus
/// list changed while some players have finished under another one; their bonuses stay (D-113) until «Пересчитать бонусы
/// по текущим правилам».
/// </summary>
public sealed record RulesChangeResult(int Version, IReadOnlyList<string> Warnings);

/// <summary>
/// A new ruleset from the admin's editor: the whole document, the version it was edited from, and a command id
/// generated once per save.
/// </summary>
public sealed record ChangeRulesRequest(Guid CommandId, int ExpectedVersion, Ruleset Ruleset);

/// <summary>An invalid ruleset: every problem with the JSON path of its field (C2).</summary>
public sealed record RulesetProblem(string Title, int Status, IReadOnlyList<RulesetError> Errors);

public static class RulesEndpoints
{
    public static void MapRules(this RouteGroupBuilder api)
    {
        api.MapGet("/seasons/{seasonId:guid}/rules", GetRulesAsync)
            .WithTags("Rules")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        api.MapPut("/admin/seasons/{seasonId:guid}/rules", ChangeRulesAsync)
            .WithTags("Admin")
            .RequireAuthorization(Policies.Admin)
            // The body is read by hand (strict parse with field paths); this documents its shape for the client.
            .Accepts<ChangeRulesRequest>("application/json")
            .Produces<RulesetProblem>(StatusCodes.Status400BadRequest, "application/problem+json")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<Seasons.RejectionProblem>(StatusCodes.Status409Conflict, "application/problem+json");
    }

    private static async Task<Results<Ok<RulesView>, NotFound>> GetRulesAsync(Guid seasonId, GameEventDbContext db, CancellationToken ct)
    {
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.SeasonId == seasonId && (e.Type == "season-created" || e.Type == "ruleset-changed"))
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return TypedResults.NotFound();
        }

        var history = new List<RulesVersionView>();
        Ruleset? previous = null;
        var version = 0;
        foreach (var row in rows)
        {
            var (number, ruleset) = EventCodec.Decode(new StoredEvent(row.Type, row.Version, row.Data)) switch
            {
                SeasonCreated created => (1, created.Ruleset),
                RulesetChanged changed => (changed.Version, changed.Ruleset),
                var other => throw new InvalidOperationException($"Unexpected event {other.GetType().Name}."),
            };
            history.Add(new RulesVersionView(
                number, row.OccurredAt, row.AuthorId, previous is null ? [] : RulesetDiff.Between(previous, ruleset)));
            previous = ruleset;
            version = number;
        }

        history.Reverse(); // newest first
        return TypedResults.Ok(new RulesView(version, previous!, history));
    }

    private static async Task<Results<Ok<RulesChangeResult>, JsonHttpResult<RulesetProblem>, NotFound, ProblemHttpResult, ValidationProblem>> ChangeRulesAsync(
        Guid seasonId, HttpRequest http, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct)
    {
        JsonObject? body;
        try
        {
            body = (await JsonNode.ParseAsync(http.Body, cancellationToken: ct)) as JsonObject;
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body is null
            || body["commandId"]?.GetValueKind() != JsonValueKind.String
            || !Guid.TryParse(body["commandId"]!.GetValue<string>(), out var commandId)
            || commandId == Guid.Empty
            || body["expectedVersion"]?.GetValueKind() != JsonValueKind.Number
            || body["ruleset"] is not JsonObject rulesetJson)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["body"] = ["Expected {commandId, expectedVersion, ruleset}."],
            });
        }

        var expectedVersion = body["expectedVersion"]!.GetValue<int>();
        if (!await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        Ruleset ruleset;
        try
        {
            ruleset = RulesetJson.Parse(rulesetJson.ToJsonString());
        }
        catch (JsonException e)
        {
            return Invalid([new RulesetError(Path(e.Path), e.Message)]);
        }

        var errors = RulesetValidator.Validate(ruleset).ToList();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(ruleset.Season.Timezone, out _))
        {
            // The engine cannot read the system time zone database; the site checks it before the command.
            errors.Add(new RulesetError("season.timezone", $"'{ruleset.Season.Timezone}' is not a known IANA time zone"));
        }

        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        // The client's command id makes a double submit one change; the expected version refuses a save made
        // from a stale editor instead of overwriting someone else's change.
        var outcome = await bus.SendAsync(
            new CommandEnvelope(commandId, seasonId, new ChangeRuleset(ruleset, expectedVersion), user.UserId()), ct);
        if (!outcome.IsAccepted)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The rules were not changed.",
                detail: outcome.Rejection!.Detail,
                extensions: new Dictionary<string, object?> { ["code"] = outcome.Rejection.Code });
        }

        var changed = outcome.Events.Select(e => e.Event).OfType<RulesetChanged>().Single();
        return TypedResults.Ok(new RulesChangeResult(changed.Version, await WarningsAsync(seasonId, changed.Ruleset, db, ct)));
    }

    public const string BonusesKept = "finish.bonusesKept";

    /// <summary>D-113: finishers who hold a bonus list other than the one now in force keep their bonuses.</summary>
    private static async Task<IReadOnlyList<string>> WarningsAsync(Guid seasonId, Ruleset ruleset, GameEventDbContext db, CancellationToken ct)
    {
        var current = FinishBonusRules.Of(ruleset.Finish);
        var tables = await db.SeasonPlayers.AsNoTracking()
            .Where(p => p.SeasonId == seasonId && p.FinishOrder != null)
            .Select(p => p.FinishBonusRulesJson)
            .ToListAsync(ct);
        return tables.Any(t => t is null || JsonSerializer.Deserialize<FinishBonusRules>(t, EngineJson.Options) != current) ? [BonusesKept] : [];
    }

    private static JsonHttpResult<RulesetProblem> Invalid(IReadOnlyList<RulesetError> errors) =>
        TypedResults.Json(
            new RulesetProblem("The ruleset is invalid.", StatusCodes.Status400BadRequest, errors),
            statusCode: StatusCodes.Status400BadRequest,
            contentType: "application/problem+json");

    // "$.reward.diceCount" → "reward.diceCount"; no path (the document itself) → "ruleset".
    private static string Path(string? jsonPath) =>
        jsonPath is null or "$" ? "ruleset" : jsonPath.StartsWith("$.", StringComparison.Ordinal) ? jsonPath[2..] : jsonPath.TrimStart('$');
}
