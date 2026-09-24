using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Runs;
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

        var player = state.Players[command.PlayerId];

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
            1 => Decision.Accept(new GameRolled(player.PlayerId, category.Name, [.. misses], offers[0].GameId, offers[0].Snapshot, now)),
            _ => Decision.Accept(new GameChoiceRolled(player.PlayerId, category.Name, [.. misses], context.Ids.NewId(), [.. offers])),
        };
    }

    public static Decision Decide(SeasonState state, DeclareAlreadyPlayed command, EngineContext context) =>
        throw new NotImplementedException("C5");

    public static SeasonState Apply(SeasonState state, GameExcluded e) =>
        throw new NotImplementedException("C5");

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
