using System.Security.Claims;
using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Pool;
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
/// Use an item of my inventory (D-407, D-441): <c>targetPlayerId</c> — the target of a «chosen» item (one of the item's
/// <c>targets</c>), <c>choices</c> — the answers to its <c>steps</c> in order (an option's id, an object's instance id).
/// </summary>
public sealed record UseItemRequest(Guid CommandId, Guid? TargetPlayerId = null, IReadOnlyList<string?>? Choices = null);

/// <summary>Roll my shop (D-403): a free roll first, then coins.</summary>
public sealed record ShopRollRequest(Guid CommandId);

/// <summary>Buy lot <c>lot</c> (its index in the offer) while the offer lasts.</summary>
public sealed record BuyLotRequest(Guid CommandId, int Lot);

/// <summary>Bet <c>stake</c> coins that <c>onPlayerId</c> completes their current run within <c>days</c> days (D-414).</summary>
public sealed record PlaceBetRequest(Guid CommandId, Guid OnPlayerId, int Days, int Stake);

/// <summary>
/// My economy in the season (D-441): coins, the inventory with what each object allows now when <c>features.items</c> is
/// on (null when off: bets play without items), the shop when <c>features.shop</c> is on, the bets when
/// <c>features.bets</c> is on. <c>inventoryLimit</c> counts items only (D-406).
/// </summary>
public sealed record EconomyView(
    int Coins,
    int InventoryLimit,
    // The shop first: the OpenAPI document keeps the first use of a shared enum, and a lot's rarity is never null
    ShopView? Shop,
    BetsView? Bets,
    IReadOnlyList<InventoryItemView>? Items);

/// <summary>
/// An object I hold: its definition's name and description, what is left of its lifetime, who put it on me. <c>usable</c> —
/// «Использовать» is open now; <c>refusal</c> — the engine's code when it is not (wrong window, deadline, frozen inventory,
/// no target; an effect or a special roll acts on its own). <c>targets</c> — when <c>choosesTarget</c>, exactly the players
/// the engine accepts; <c>steps</c> — the answers asked, in order (D-440).
/// </summary>
public sealed record InventoryItemView(
    Guid InstanceId,
    string ObjectId,
    ObjectKind Kind,
    string Name,
    string Description,
    Rarity? Rarity,
    UseWindow? Window,
    bool Hostile,
    int? UsesLeft,
    int? RunsLeft,
    DateTimeOffset? ExpiresAt,
    string? FromPlayer,
    bool Usable,
    string? Refusal,
    bool ChoosesTarget,
    IReadOnlyList<ItemTargetView> Targets,
    IReadOnlyList<ItemStepView> Steps);

/// <summary>A player an item may target: the name and the token colour (as on the map).</summary>
public sealed record ItemTargetView(Guid PlayerId, string Name, int Token);

/// <summary>
/// An answer to give: a choice (<c>options</c>), a direction (<c>options</c> <c>forward</c> and <c>back</c>, the screen
/// names them) or objects of the target (<c>objectsByTarget</c>: for each possible target, its objects that fit) —
/// <c>count</c> different ones (all of them when fewer), or with <c>ranked</c> any number in order of preference, the rest
/// taken at random (D-476). <c>prompt</c> — the content's question.
/// </summary>
public sealed record ItemStepView(
    ItemStepKind Kind, string? Prompt, IReadOnlyList<ItemOptionView> Options, IReadOnlyList<TargetObjectsView>? ObjectsByTarget, int Count, bool Ranked);

/// <summary>One answer: the id the engine checks, the words the screen shows.</summary>
public sealed record ItemOptionView(string Id, string Label);

public sealed record TargetObjectsView(Guid PlayerId, IReadOnlyList<ItemOptionView> Objects);

/// <summary>
/// My shop (D-443): the next roll's payment and price, free rolls held, why a roll is refused now (the engine's code), and
/// the lots while the offer lasts (<c>expiresAt</c> by the server's clock).
/// </summary>
public sealed record ShopView(ShopPayment Payment, int Price, int FreeRolls, string? Refusal, DateTimeOffset? ExpiresAt, IReadOnlyList<ShopLotView> Lots);

/// <summary>A lot of my offer; <c>refusal</c> — the engine's code when buying it is refused now (sold, too few coins, the inventory full…), D-447.</summary>
public sealed record ShopLotView(int Lot, string ObjectId, string Name, string Description, Rarity Rarity, int Price, bool Sold, string? Refusal);

/// <summary>
/// My bets and the rules of a new one: deadlines in days and the largest stake of the season (D-414). From the bets plan
/// (D-447): <c>refusal</c> — the engine's code when no bet is placed now, <c>openBets</c> of <c>maxOpenBets</c>,
/// <c>stakeLimit</c> — the largest stake I can place (the season's limit or my coins), <c>runs</c> — exactly the runs a
/// bet is accepted on now.
/// </summary>
public sealed record BetsView(
    IReadOnlyList<BetView> Bets,
    IReadOnlyList<int> DeadlineOptionsDays,
    int MaxStake,
    string? Refusal,
    int OpenBets,
    int MaxOpenBets,
    int StakeLimit,
    IReadOnlyList<BetRunView> Runs);

public sealed record BetView(
    Guid BetId, Guid OnPlayerId, string OnPlayer, Guid RunId, DateTimeOffset PlacedAt, DateTimeOffset Deadline, int Stake, decimal Multiplier, BetStatus Status, int Payout, string GameTitle);

/// <summary>
/// A run I may bet on: whose (name and token colour), the game and its hours of the roll's snapshot, when bets on it close,
/// and the multiplier of each deadline.
/// </summary>
public sealed record BetRunView(
    Guid PlayerId, string Name, int Token, Guid RunId, Guid GameId, string GameTitle, decimal? Hours, DateTimeOffset ClosesAt, IReadOnlyList<BetOptionView> Options);

public sealed record BetOptionView(int Days, decimal Multiplier);

/// <summary>
/// The player's economy over HTTP (stage 4, D-441): the view of my inventory, shop and bets, and the actions — use an item,
/// roll the shop, buy a lot, place a bet. Every action is a command of the queue with the client's <c>commandId</c>; the
/// player acts only for themselves (the id comes from the signed-in account).
/// </summary>
public static class EconomyEndpoints
{
    /// <summary>The most answers one use carries; more never reach the engine.</summary>
    public const int MaxChoices = 10;

    /// <summary>An answer is an option id (a category's name, a game's or a player's id) or an instance id.</summary>
    public const int MaxChoiceLength = 100;

    public static void MapEconomy(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var seasons = api.MapGroup("/seasons/{seasonId:guid}").WithTags("Economy").RequireAuthorization();
        seasons.MapGet("/economy", GetAsync)
            .RequireRateLimiting(AppSetup.SeasonReadRateLimit)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .Produces<RejectionProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        seasons.MapPost("/items/{instanceId:guid}/use", (Guid seasonId, Guid instanceId, UseItemRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                ChoicesInvalid(request.Choices) is { } invalid
                    ? Task.FromResult<ActionResult>(invalid)
                    : ActAsync(seasonId, request.CommandId, user, db, bus, p => new UseItem(p, instanceId, request.TargetPlayerId, [.. (request.Choices ?? []).OfType<string>()]), ct))
            .RequireAuthorization(Policies.Player)
            .WithActionErrors();

        seasons.MapPost("/shop/roll", (Guid seasonId, ShopRollRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                ActAsync(seasonId, request.CommandId, user, db, bus, p => new RollShop(p), ct))
            .RequireAuthorization(Policies.Player)
            .WithActionErrors();

        seasons.MapPost("/shop/buy", (Guid seasonId, BuyLotRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                request.Lot < 0
                    ? Task.FromResult<ActionResult>(Invalid("lot", "A lot's index starts from 0."))
                    : ActAsync(seasonId, request.CommandId, user, db, bus, p => new BuyLot(p, request.Lot), ct))
            .RequireAuthorization(Policies.Player)
            .WithActionErrors();

        seasons.MapPost("/bets", (Guid seasonId, PlaceBetRequest request, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, CancellationToken ct) =>
                BetInvalid(request) is { } invalid
                    ? Task.FromResult<ActionResult>(invalid)
                    : ActAsync(seasonId, request.CommandId, user, db, bus, p => new PlaceBet(p, request.OnPlayerId, request.Days, request.Stake), ct))
            .RequireAuthorization(Policies.Player)
            .WithActionErrors();
    }

    internal static ValidationProblem? ChoicesInvalid(IReadOnlyList<string?>? choices) =>
        choices is null
            ? null
            : choices.Count > MaxChoices
                ? Invalid("choices", $"At most {MaxChoices} answers.")
                : choices.Any(c => c is not { Length: > 0 and <= MaxChoiceLength } || c.Any(char.IsControl))
                    ? Invalid("choices", $"Every answer is 1–{MaxChoiceLength} characters.")
                    : null;

    private static ValidationProblem? BetInvalid(PlaceBetRequest request) =>
        request.OnPlayerId == Guid.Empty
            ? Invalid("onPlayerId", "A player to bet on is required.")
            : request.Days < 1
                ? Invalid("days", "The deadline is a number of days.")
                : request.Stake < 1
                    ? Invalid("stake", "A stake is at least 1 coin.")
                    : null;

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static async Task<ActionResult> ActAsync(
        Guid seasonId, Guid commandId, ClaimsPrincipal user, GameEventDbContext db, CommandBus bus, Func<Guid, ICommand> command, CancellationToken ct)
    {
        if (await SeasonEndpoints.PrecheckAsync(seasonId, commandId, db, ct) is { } refused)
        {
            return refused;
        }

        var userId = user.UserId();
        var playerId = await db.SeasonPlayers
            .Where(p => p.SeasonId == seasonId && p.UserId == userId)
            .Select(p => (Guid?)p.Id)
            .SingleOrDefaultAsync(ct);
        return playerId is null
            ? TypedResults.Forbid()
            : await SeasonEndpoints.SendAsync(seasonId, commandId, command(playerId.Value), userId, bus, ct);
    }

    private static async Task<Results<Ok<EconomyView>, NotFound, ForbidHttpResult, ProblemHttpResult>> GetAsync(
        Guid seasonId, ClaimsPrincipal user, GameEventDbContext db, IClock clock, CancellationToken ct)
    {
        if (seasonId == Guid.Empty || !await db.Seasons.AnyAsync(s => s.Id == seasonId, ct))
        {
            return TypedResults.NotFound();
        }

        var userId = user.UserId();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
        if (state.Players.Values.FirstOrDefault(p => p.UserId == userId) is not { } me)
        {
            return TypedResults.Forbid();
        }

        // The mechanic switched off is not shown at all (CLAUDE.md «Выключенная механика не видна в интерфейсе»); bets
        // play without items (RulesetSupport), so the view is refused only with all three off
        var features = state.Rules.Features;
        if (!features.Items && !features.Shop && !features.Bets)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The mechanic is off.",
                detail: "Items, the shop and bets are off in this season.",
                extensions: new Dictionary<string, object?> { ["code"] = RejectionCodes.FeatureDisabled });
        }

        var now = clock.UtcNow;
        var pool = await PoolReader.LoadAsync(db, ct);
        var tokens = await db.TokensAsync([seasonId], ct);
        var names = state.Players.ToDictionary(p => p.Key, p => p.Value.Name);
        ItemTargetView Target(Guid id) => new(id, names[id], tokens.GetValueOrDefault(id));

        List<InventoryItemView>? items = !features.Items ? null : [.. ItemPlans.Of(state, pool, now, me.PlayerId).Select(plan => new InventoryItemView(
            plan.Item.InstanceId,
            plan.Item.ObjectId,
            plan.Definition.Kind,
            plan.Definition.Name,
            plan.Definition.Description,
            plan.Definition.Rarity,
            plan.Definition.Window,
            plan.Definition.Hostile,
            plan.Item.UsesLeft,
            plan.Item.RunsLeft,
            plan.Item.ExpiresAt,
            plan.Item.FromPlayerId is { } from ? names.GetValueOrDefault(from) : null,
            plan.Usable,
            plan.Refusal,
            plan.ChoosesTarget,
            [.. plan.Targets.Select(Target)],
            [.. plan.Steps.Select(step => Step(step, state, pool, names))]))];

        ShopView? shop = null;
        if (features.Shop && ShopPlans.Of(state, now, me.PlayerId) is { } plan)
        {
            shop = new ShopView(
                plan.Payment,
                plan.Price,
                plan.FreeRolls,
                plan.Refusal,
                plan.Offer?.ExpiresAt,
                [.. (plan.Offer?.Lots ?? []).Select((lot, i) =>
                {
                    var definition = state.Catalog.Get(lot.ObjectId);
                    return new ShopLotView(i, lot.ObjectId, definition.Name, definition.Description, lot.Rarity, lot.Price, lot.Sold, plan.LotRefusals[i]);
                })]);
        }

        BetsView? bets = null;
        if (features.Bets && BetPlans.Of(state, now, me.PlayerId) is { } betPlan)
        {
            var titles = pool.Games.ToDictionary(g => g.Id, g => g.Title);
            string Title(Guid runId) => state.Runs.TryGetValue(runId, out var run) ? titles.GetValueOrDefault(run.GameId, "") : "";
            bets = new BetsView(
                [.. me.Wallet.Bets.OrderByDescending(b => b.PlacedAt).Select(b => new BetView(
                    b.BetId, b.OnPlayerId, names.GetValueOrDefault(b.OnPlayerId, ""), b.RunId, b.PlacedAt, b.Deadline, b.Stake, b.Multiplier, b.Status, b.Payout, Title(b.RunId)))],
                [.. state.Rules.Bets.DeadlineOptionsDays],
                state.Rules.Bets.MaxStake,
                betPlan.Refusal,
                betPlan.OpenBets,
                betPlan.MaxOpenBets,
                betPlan.MaxStake,
                [.. betPlan.Runs.Select(r => new BetRunView(
                    r.PlayerId,
                    names[r.PlayerId],
                    tokens.GetValueOrDefault(r.PlayerId),
                    r.RunId,
                    r.GameId,
                    titles.GetValueOrDefault(r.GameId, ""),
                    r.Hours,
                    r.ClosesAt,
                    [.. r.Options.Select(o => new BetOptionView(o.Days, o.Multiplier))]))]);
        }

        return TypedResults.Ok(new EconomyView(me.Coins, state.Rules.Economy.InventoryLimit, shop, bets, items));
    }

    // An answer in words: a category is its own name; a player, a game and an object are named by their id
    private static ItemStepView Step(ItemStep step, SeasonState state, Engine.Pool.PoolSnapshot pool, Dictionary<Guid, string> names)
    {
        var games = step.From == ChoiceSource.Games ? pool.Games.ToDictionary(g => g.Id.ToString("N"), g => g.Title) : null;
        string Label(string option) => step.From switch
        {
            ChoiceSource.Players when Guid.TryParseExact(option, "N", out var id) => names.GetValueOrDefault(id, option),
            ChoiceSource.Games => games!.GetValueOrDefault(option, option),
            _ => option,
        };

        return new ItemStepView(
            step.Kind,
            step.Prompt,
            [.. step.Options.Select(o => new ItemOptionView(o, Label(o)))],
            step.ObjectsByTarget is { } byTarget
                ? [.. byTarget.Select(t => new TargetObjectsView(
                    t.Key,
                    [.. t.Value.Select(o => new ItemOptionView(
                        o,
                        state.Players[t.Key].Wallet.Inventory.FirstOrDefault(x => x.InstanceId.ToString("N") == o) is { } held
                            ? state.Catalog.Get(held.ObjectId).Name
                            : o))]))]
                : null,
            step.Count,
            step.Ranked);
    }
}
