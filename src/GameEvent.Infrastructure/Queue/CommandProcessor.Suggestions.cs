using System.Security.Cryptography;
using GameEvent.Engine.Suggestions;
using Microsoft.Extensions.Logging;

namespace GameEvent.Infrastructure.Queue;

/// <summary>
/// An approved game suggestion reaches the pool (D-527, D-572): the pool is the global log's (D-119), so the season's engine
/// only marks the suggestion approved, and the queue then adds its card by the pool's own command — with the suggester as
/// its author and the same check of duplicates as adding by hand. A refusal (the game is already in the pool) leaves the
/// approval standing. A repeat of the approval (the same command id) sends the pool's command again: its id comes from the
/// suggestion, so a game already added is a duplicate, and one lost by a stop in between is added now (D-817).
/// </summary>
public sealed partial class CommandProcessor
{
    private async Task AddApprovedGamesAsync(Guid seasonId, CommandOutcome outcome, CancellationToken ct)
    {
        foreach (var reviewed in outcome.Events.Select(e => e.Event).OfType<SuggestionReviewed>().Where(r => r.Status == SuggestionStatus.Approved))
        {
            // A repeat or a failure before it left no cached state: the season's state comes from its log (D-817)
            if (!_cache.TryGetValue(seasonId, out var cached))
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                cached = await EventLog.EventLogReader.ReplaySeasonAsync(db, seasonId, ct);
                _cache[seasonId] = cached;
            }

            if (cached.State.Suggestions.GetValueOrDefault(reviewed.SuggestionId) is not { Kind: SuggestionKind.Game, Game: { } card } suggestion)
            {
                continue;
            }

            // The id comes from the suggestion: a repeat is a duplicate, never a second game
            var id = new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"suggested-game:{suggestion.SuggestionId:N}")).AsSpan(0, 16));
            var envelope = new CommandEnvelope(id, Guid.Empty, new Pool.AddGame(card, suggestion.UserId, Force: false), AuthorId: null);
            CommandOutcome added;
            try
            {
                added = await ProcessAsync(envelope, ct);
            }
#pragma warning disable CA1031 // the approval is committed: a failed addition must not fail it
            catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogCommandFailed(logger, e, envelope.CommandId, nameof(Pool.AddGame));
                continue;
            }

            if (!added.IsAccepted)
            {
                LogSuggestedGameRefused(logger, suggestion.SuggestionId, added.Rejection?.Code);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The approved suggestion {SuggestionId} did not add its game to the pool: {Code}")]
    private static partial void LogSuggestedGameRefused(ILogger logger, Guid suggestionId, string? code);
}
