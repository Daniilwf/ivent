using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;

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

internal static class Rolling
{
    public static Decision Decide(SeasonState state, RollGame command, EngineContext context)
    {
        if (!state.IsCreated)
        {
            return Decision.Reject(RejectionCodes.SeasonNotCreated, "Create the season first.");
        }

        if (!state.Players.TryGetValue(command.PlayerId, out var player))
        {
            return Decision.Reject(RejectionCodes.PlayerUnknown, $"Player {command.PlayerId} is not in the season.");
        }

        if (player.Phase != TurnPhase.Idle)
        {
            return Decision.Reject(RejectionCodes.WrongPhase, $"Roll needs phase Idle, player is {player.Phase}.");
        }

        // Pool order is whatever storage returns; sort so the same seed gives the same log (invariant 14).
        var status = SeasonGameStatus.For(state, player.PlayerId);
        var candidates = context.Pool.Games
            .Where(g => status.Of(g) != GameAvailability.Hidden)
            .OrderBy(g => g.Id)
            .ToList();

        // The wheel spins only over categories where at least one game is available (SPEC «Уточнения»: Ролл).
        var wheel = context.Pool.Categories
            .Where(c => c.Weight > 0 && candidates.Any(g => InCategory(g, c) && status.Of(g) == GameAvailability.Available))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        if (wheel.Count == 0)
        {
            return Decision.Reject(RejectionCodes.NoAvailableGames, "No category has an available game.");
        }

        var category = SpinWheel(wheel, context.Random);

        // Draw games of the category without replacement; misses are logged, the first available wins (D-46).
        // The loop ends: the wheel and the draw share one predicate, so the category has an available game.
        var remaining = candidates.Where(g => InCategory(g, category)).ToList();
        var misses = new List<RollMiss>();
        while (true)
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

            return Decision.Accept(new GameRolled(
                player.PlayerId, category.Name, [.. misses], game.Id, snapshot, context.Clock.UtcNow));
        }
    }

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

    private static bool InCategory(Game game, Category category) =>
        game.Tags.Contains(category.Name, StringComparer.OrdinalIgnoreCase);

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
