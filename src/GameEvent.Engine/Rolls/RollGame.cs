using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;

namespace GameEvent.Engine.Rolls;

/// <summary>
/// "Spin the wheel": the server picks a category among those with available games, then a game (D-05).
/// The player moves Idle → Rolling with the game offered and reserved.
/// </summary>
public sealed record RollGame(Guid PlayerId) : ICommand;

/// <summary>Why the wheel landed on a game the player cannot take.</summary>
public enum RollMissReason
{
    /// <summary>Someone already completed it this season: «Уже прошёл Вася».</summary>
    CompletedInSeason,

    /// <summary>Someone is playing it or has it offered: «Сейчас играет Вася».</summary>
    BeingPlayed,
}

/// <summary>A wheel miss recorded in the log; not a reroll.</summary>
public sealed record RollMiss(Guid GameId, RollMissReason Reason, Guid ByPlayerId);

/// <summary>The rolled game waiting for the player to start it, with rules fixed at roll time.</summary>
public sealed record RollOffer(Guid GameId, RunSnapshot Snapshot, DateTimeOffset RolledAt);

[EventType("game-rolled")]
public sealed record GameRolled(
    Guid PlayerId,
    string Category,
    EquatableArray<RollMiss> Misses,
    Guid GameId,
    RunSnapshot Snapshot,
    DateTimeOffset RolledAt) : IGameEvent;

/// <summary>
/// The wheel with <c>roll.choiceCount</c> &gt; 1 (D-06): up to that many available games of one category, drawn without
/// replacement, reserved until the player picks one with <see cref="Turns.MakeChoice"/>. When the category has only
/// one available game the roll is a plain <see cref="GameRolled"/>.
/// </summary>
[EventType("game-choice-rolled")]
public sealed record GameChoiceRolled(
    Guid PlayerId,
    string Category,
    EquatableArray<RollMiss> Misses,
    Guid ChoiceId,
    EquatableArray<RollOffer> Offers) : IGameEvent;

internal static class Rolling
{
    public static Decision Decide(SeasonState state, RollGame command, EngineContext context)
    {
        if (TurnRules.Check(state, command.PlayerId, command) is { } rejection)
        {
            return rejection;
        }

        return Draw(state, command.PlayerId, context, Filters(state)) is { } roll
            ? Decision.Accept(roll)
            : Decision.Reject(RejectionCodes.NoAvailableGames, "No category has an available game.");
    }

    public static Decision Decide(SeasonState state, DeclareAlreadyPlayed command, EngineContext context)
    {
        if (TurnRules.Check(state, command.PlayerId, command) is { } rejection)
        {
            return rejection;
        }

        var player = state.Players[command.PlayerId];
        if (!IsOffered(player, command.GameId))
        {
            return Decision.Reject(RejectionCodes.GameNotOffered, $"Game {command.GameId} is not offered to the player.");
        }

        // The exclusion drops the offer or the choice; the free roll then spins over what is left (D-07, D-92).
        var excluded = new GameExcluded(player.PlayerId, command.GameId, ExclusionReason.AlreadyPlayed);
        var after = Apply(state, excluded);
        return Draw(after, player.PlayerId, context, Filters(after)) is { } roll
            ? Decision.Accept(excluded, roll)
            : Decision.Accept(excluded);
    }

    public static Decision Decide(SeasonState state, Reroll command, EngineContext context)
    {
        if (TurnRules.Check(state, command.PlayerId, command) is { } rejection)
        {
            return rejection;
        }

        var player = state.Players[command.PlayerId];
        var rules = state.Rules.Roll;
        var cost = rules.RerollCost;

        // D-07, D-93: a free reroll of this roll, then a coupon, then the price.
        var payment = player.RerollsThisRoll < rules.FreeRerollsPerRoll ? RerollPayment.FreeThisRoll
            : player.Resources[FreeRerollsResource] > 0 ? RerollPayment.FreeRerollResource
            : cost.Kind == RerollCostKind.BadEvent ? RerollPayment.BadEvent
            : RerollPayment.Coins;
        var price = cost.Amount ?? 0;
        if (payment == RerollPayment.Coins && player.Coins < price)
        {
            return Decision.Reject(RejectionCodes.NotEnoughCoins, $"A reroll costs {price} coins, the player has {player.Coins}.");
        }

        var givenUp = player.Offer is { } offer
            ? new HashSet<Guid> { offer.GameId }
            : player.Choice!.Options.Select(o => o.Game?.GameId).OfType<Guid>().ToHashSet();
        var rerolled = new GameRerolled(player.PlayerId, [.. givenUp.Order()], payment);
        var after = Apply(state, rerolled);
        if (Draw(after, player.PlayerId, context, Filters(after), givenUp) is not { } roll)
        {
            return Decision.Reject(RejectionCodes.NoAvailableGames, "No game is left besides the ones given up.");
        }

        IGameEvent? paid = payment switch
        {
            RerollPayment.FreeRerollResource => new ResourceChanged(player.PlayerId, FreeRerollsResource, -1, ResourceReason.Reroll),
            RerollPayment.Coins when price != 0 => new CoinsChanged(player.PlayerId, -price, CoinsReason.Reroll, RunId: null),
            RerollPayment.BadEvent => new ManualEffectCreated(context.Ids.NewId(), player.PlayerId, EventKind.Bad, ManualEffectSource.PaidReroll, RunId: null),
            _ => null,
        };
        return Decision.Accept(paid is null ? [rerolled, roll] : [rerolled, paid, roll]);
    }

    /// <summary>The reroll coupon resource (CONTENT.md «Купон реролла»).</summary>
    public const string FreeRerollsResource = "freeRerolls";

    public static SeasonState Apply(SeasonState state, GameRerolled e)
    {
        var player = state.Players[e.PlayerId];
        return state with
        {
            Players = state.Players.SetItem(
                e.PlayerId, player with { Offer = null, Choice = null, RerollsThisRoll = player.RerollsThisRoll + 1 }),
        };
    }

    public static SeasonState Apply(SeasonState state, GameExcluded e)
    {
        var player = state.Players[e.PlayerId];
        if (player.Exclusions.Any(x => x.GameId == e.GameId))
        {
            // An excluded game is hidden from the player, so it can never be excluded twice (D-92).
            throw new InvalidOperationException($"Game {e.GameId} is already excluded for player {e.PlayerId}.");
        }

        var exclusions = player.Exclusions.Append(new GameExclusion(e.GameId, e.Reason)).OrderBy(x => x.GameId);
        player = player with { Exclusions = [.. exclusions] };
        if (IsOffered(player, e.GameId))
        {
            player = player with { Phase = TurnPhase.Idle, Offer = null, Choice = null, RerollsThisRoll = 0 };
        }

        return state with { Players = state.Players.SetItem(e.PlayerId, player) };
    }

    private static bool IsOffered(SeasonPlayer player, Guid gameId) =>
        player.Offer?.GameId == gameId || (player.Choice?.Options.Any(o => o.Game?.GameId == gameId) ?? false);

    /// <summary>Whether a roll of <paramref name="playerId"/> would find a game: the same test the wheel makes.</summary>
    internal static bool CanRoll(SeasonState state, Guid playerId, IPoolView pool, IReadOnlyList<RollFilter> filters) =>
        Wheel(state, playerId, pool, filters).Categories.Count > 0;

    /// <summary>
    /// The wheel and the draw (D-05, D-06, D-46): the roll event for <paramref name="playerId"/>, or null when no
    /// category has an available game under the filters.
    /// </summary>
    internal static IGameEvent? Draw(
        SeasonState state, Guid playerId, EngineContext context, IReadOnlyList<RollFilter> filters, IReadOnlySet<Guid>? givenUp = null)
    {
        var (status, candidates, wheel) = Wheel(state, playerId, context.Pool, filters, givenUp);
        if (wheel.Count == 0)
        {
            return null;
        }

        var category = SpinWheel(wheel, context.Random);

        // Draw games of the category without replacement until choiceCount are available or the category runs out;
        // misses are logged (D-46). The wheel and the draw share one predicate, so at least one game is found.
        var remaining = candidates.Where(g => InCategory(g, category)).ToList();
        var misses = new List<RollMiss>();
        var offers = new List<RollOffer>();
        var now = context.Clock.UtcNow;
        while (offers.Count < state.Rules.Roll.ChoiceCount && remaining.Count > 0)
        {
            var index = context.Random.NextInt(0, remaining.Count);
            var game = remaining[index];
            remaining.RemoveAt(index);

            if (status.Of(game, out var miss) == GameAvailability.Miss)
            {
                misses.Add(miss!);
                continue;
            }

            var snapshot = new RunSnapshot(
                state.RulesetVersion,
                game.Hours,
                state.Rules.Reward.DiceCount,
                state.Rules.Reward.DieByDifficulty);
            offers.Add(new RollOffer(game.Id, snapshot, now));
        }

        return offers.Count switch
        {
            0 => throw new InvalidOperationException($"Category '{category.Name}' was on the wheel without an available game."),
            1 => new GameRolled(playerId, category.Name, [.. misses], offers[0].GameId, offers[0].Snapshot, now),
            _ => new GameChoiceRolled(playerId, category.Name, [.. misses], context.Ids.NewId(), [.. offers]),
        };
    }

    // The candidates under the filters and the categories on the wheel: those with an available game among them
    // (SPEC «Уточнения»: Ролл). A game counts for a filter only if it is available and on the wheel (weight > 0).
    // Games a reroll just gave up are left out silently, like the player's own exclusions (D-93).
    private static (SeasonGameStatus Status, List<Game> Candidates, List<Category> Categories) Wheel(
        SeasonState state, Guid playerId, IPoolView pool, IReadOnlyList<RollFilter> filters, IReadOnlySet<Guid>? givenUp = null)
    {
        // Pool order is whatever storage returns; sort so the same seed gives the same log (invariant 14).
        var status = SeasonGameStatus.For(state, playerId);
        var weighted = pool.Categories.Where(c => c.Weight > 0).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        var visible = pool.Games
            .Where(g => status.Of(g) != GameAvailability.Hidden && givenUp?.Contains(g.Id) != true)
            .OrderBy(g => g.Id)
            .ToList();
        bool Rollable(Game g) => status.Of(g) == GameAvailability.Available && weighted.Any(c => InCategory(g, c));

        var candidates = RollFilters.Apply(visible, Rollable, filters).ToList();
        var categories = weighted.Where(c => candidates.Any(g => InCategory(g, c) && Rollable(g))).ToList();
        return (status, candidates, categories);
    }

    // Stage 1 has no live filters: the length limit of the last days is not supported yet, zones come in stage 2 (D-92).
    internal static IReadOnlyList<RollFilter> Filters(SeasonState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return [];
    }

    /// <summary>Whether <paramref name="game"/> is on <paramref name="category"/>'s slice of the wheel (tags ignore case).</summary>
    internal static bool InCategory(Game game, Category category) =>
        game.Tags.Contains(category.Name, StringComparer.OrdinalIgnoreCase);

    public static SeasonState Apply(SeasonState state, GameRolled e) =>
        state with
        {
            Players = state.Players.SetItem(
                e.PlayerId,
                state.Players[e.PlayerId] with
                {
                    Phase = TurnPhase.Rolling,
                    Offer = new RollOffer(e.GameId, e.Snapshot, e.RolledAt),
                }),
        };

    private static Category SpinWheel(List<Category> wheel, IRandomSource random)
    {
        var ticket = random.NextInt(0, wheel.Sum(c => c.Weight));
        foreach (var category in wheel)
        {
            if (ticket < category.Weight)
            {
                return category;
            }

            ticket -= category.Weight;
        }

        throw new InvalidOperationException("Wheel ticket is outside the total weight.");
    }
}
