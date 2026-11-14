using System.Collections.Immutable;
using System.Text.Json;
using GameEvent.Engine.Challenges;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Suggestions;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Seasons;

/// <summary>
/// The projection of the stage 5 mechanics (migration «Events», task 5.16, D-810, D-811): polls with their options and votes,
/// global events, invitations, requests of the partner board, weekly challenges with their completions, suggestions. Only
/// what a command changed is written; what an undo took back leaves its table.
/// </summary>
internal static partial class SeasonProjection
{
    private static async Task WriteEventsAsync(GameEventDbContext db, SeasonState before, SeasonState after, CancellationToken ct)
    {
        foreach (var id in Changed(before.Polls, after.Polls))
        {
            await WritePollAsync(db, after.SeasonId, id, before.Polls.GetValueOrDefault(id), after.Polls.GetValueOrDefault(id), ct);
        }

        foreach (var id in Changed(before.GlobalEvents, after.GlobalEvents))
        {
            var row = await db.GlobalEvents.FindAsync([id], ct);
            if (after.GlobalEvents.GetValueOrDefault(id) is not { } ev)
            {
                db.GlobalEvents.Remove(row ?? throw Missing("global event", id));
                continue;
            }

            if (row is null)
            {
                row = new GlobalEventRecord { Id = id, SeasonId = after.SeasonId, ObjectId = ev.ObjectId, DataJson = "" };
                db.GlobalEvents.Add(row);
            }

            row.ObjectId = ev.ObjectId;
            row.Status = ev.Status;
            row.StartedAt = ev.StartedAt;
            row.EndsAt = ev.EndsAt;
            row.Reached = ev.Reached;
            row.DataJson = JsonSerializer.Serialize(ev, EngineJson.Options);
        }

        foreach (var id in Changed(before.Interactions, after.Interactions))
        {
            var row = await db.Interactions.FindAsync([id], ct);
            if (after.Interactions.GetValueOrDefault(id) is not { } interaction)
            {
                db.Interactions.Remove(row ?? throw Missing("interaction", id));
                continue;
            }

            if (row is null)
            {
                row = new InteractionRecord { Id = id, SeasonId = after.SeasonId };
                db.Interactions.Add(row);
            }

            row.Kind = interaction.Kind;
            row.FromPlayerId = interaction.FromPlayerId;
            row.ToPlayerId = interaction.ToPlayerId;
            row.RunId = interaction.RunId;
            row.CreatedAt = interaction.CreatedAt;
            row.ExpiresAt = interaction.ExpiresAt;
            row.Status = interaction.Status;
        }

        foreach (var id in Changed(before.PartnerRequests, after.PartnerRequests))
        {
            var row = await db.PartnerRequests.FindAsync([id], ct);
            if (after.PartnerRequests.GetValueOrDefault(id) is not { } request)
            {
                db.PartnerRequests.Remove(row ?? throw Missing("partner request", id));
                continue;
            }

            if (row is null)
            {
                row = new PartnerRequestRecord { Id = id, SeasonId = after.SeasonId };
                db.PartnerRequests.Add(row);
            }

            row.PlayerId = request.PlayerId;
            row.RunId = request.RunId;
            row.CreatedAt = request.CreatedAt;
            row.ExpiresAt = request.ExpiresAt;
            row.Status = request.Status;
        }

        foreach (var id in Changed(before.WeeklyChallenges, after.WeeklyChallenges))
        {
            await WriteChallengeAsync(db, after.SeasonId, id, before.WeeklyChallenges.GetValueOrDefault(id), after.WeeklyChallenges.GetValueOrDefault(id), ct);
        }

        foreach (var id in Changed(before.Suggestions, after.Suggestions))
        {
            var row = await db.Suggestions.FindAsync([id], ct);
            if (after.Suggestions.GetValueOrDefault(id) is not { } suggestion)
            {
                db.Suggestions.Remove(row ?? throw Missing("suggestion", id));
                continue;
            }

            if (row is null)
            {
                row = new SuggestionRecord { Id = id, SeasonId = after.SeasonId, Text = "" };
                db.Suggestions.Add(row);
            }

            row.PlayerId = suggestion.PlayerId;
            row.UserId = suggestion.UserId;
            row.Kind = suggestion.Kind;
            row.Text = suggestion.Text;
            row.GameJson = suggestion.Game is null ? null : JsonSerializer.Serialize(suggestion.Game, EngineJson.Options);
            row.SubmittedAt = suggestion.SubmittedAt;
            row.Status = suggestion.Status;
            row.Comment = suggestion.Comment;
        }
    }

    private static async Task WritePollAsync(GameEventDbContext db, Guid seasonId, Guid id, Poll? old, Poll? poll, CancellationToken ct)
    {
        var row = old is null ? null : await db.Polls.FindAsync([id], ct);
        var options = old is null ? [] : await db.PollOptions.Where(o => o.PollId == id).ToListAsync(ct);
        var votes = old is null ? [] : await db.Votes.Where(v => v.PollId == id).ToListAsync(ct);
        if (poll is null)
        {
            db.Votes.RemoveRange(votes);
            db.PollOptions.RemoveRange(options);
            db.Polls.Remove(row ?? throw Missing("poll", id));
            return;
        }

        if (row is null)
        {
            row = new PollRecord { Id = id, SeasonId = seasonId, DataJson = "" };
            db.Polls.Add(row);
        }

        row.Status = poll.Status;
        row.StartedAt = poll.StartedAt;
        row.ClosesAt = poll.ClosesAt;
        row.Anonymous = poll.Anonymous;
        row.DataJson = JsonSerializer.Serialize(poll with { Options = [], Votes = [] }, EngineJson.Options);

        if (old?.Options != poll.Options)
        {
            var byPosition = options.ToDictionary(o => o.Position);
            for (var i = 0; i < poll.Options.Count; i++)
            {
                if (!byPosition.TryGetValue(i, out var option))
                {
                    option = new PollOptionRecord { PollId = id, Position = i, OptionId = "", Label = "" };
                    db.PollOptions.Add(option);
                }

                option.OptionId = poll.Options[i].Id;
                option.Label = poll.Options[i].Label;
            }

            db.PollOptions.RemoveRange(options.Where(o => o.Position >= poll.Options.Count));
        }

        if (old?.Votes != poll.Votes)
        {
            var byUser = votes.ToDictionary(v => v.UserId);
            for (var i = 0; i < poll.Votes.Count; i++)
            {
                var vote = poll.Votes[i];
                if (!byUser.Remove(vote.UserId, out var record))
                {
                    record = new VoteRecord { PollId = id, UserId = vote.UserId, OptionId = "" };
                    db.Votes.Add(record);
                }

                record.OptionId = vote.OptionId;
                record.Position = i;
            }

            db.Votes.RemoveRange(byUser.Values);
        }
    }

    private static async Task WriteChallengeAsync(
        GameEventDbContext db, Guid seasonId, Guid id, WeeklyChallengeState? old, WeeklyChallengeState? challenge, CancellationToken ct)
    {
        var row = old is null ? null : await db.WeeklyChallenges.FindAsync([id], ct);
        var completions = old is null ? [] : await db.WeeklyChallengeCompletions.Where(c => c.ChallengeId == id).ToListAsync(ct);
        if (challenge is null)
        {
            db.WeeklyChallengeCompletions.RemoveRange(completions);
            db.WeeklyChallenges.Remove(row ?? throw Missing("weekly challenge", id));
            return;
        }

        if (row is null)
        {
            row = new WeeklyChallengeRecord { Id = id, SeasonId = seasonId, DefinitionJson = "" };
            db.WeeklyChallenges.Add(row);
        }

        row.StartsAt = challenge.StartsAt;
        row.EndsAt = challenge.EndsAt;
        row.RewardCoins = challenge.RewardCoins;
        row.DefinitionJson = JsonSerializer.Serialize(challenge.Definition, EngineJson.Options);

        var byPlayer = completions.ToDictionary(c => c.PlayerId);
        for (var i = 0; i < challenge.Completions.Count; i++)
        {
            var completion = challenge.Completions[i];
            if (!byPlayer.Remove(completion.PlayerId, out var record))
            {
                record = new WeeklyChallengeCompletionRecord { ChallengeId = id, PlayerId = completion.PlayerId };
                db.WeeklyChallengeCompletions.Add(record);
            }

            record.RunId = completion.RunId;
            record.Coins = completion.Coins;
            record.Position = i;
        }

        db.WeeklyChallengeCompletions.RemoveRange(byPlayer.Values);
    }

    /// <summary>The stage 5 parts of the stored state, read back for the integrity check: columns win over the JSON.</summary>
    private static async Task<SeasonState> ReadEventsAsync(GameEventDbContext db, SeasonState state, CancellationToken ct)
    {
        var seasonId = state.SeasonId;
        var polls = await db.Polls.AsNoTracking().Where(p => p.SeasonId == seasonId).ToListAsync(ct);
        var pollIds = polls.Select(p => p.Id).ToList();
        var options = (await db.PollOptions.AsNoTracking().Where(o => pollIds.Contains(o.PollId)).ToListAsync(ct)).ToLookup(o => o.PollId);
        var votes = (await db.Votes.AsNoTracking().Where(v => pollIds.Contains(v.PollId)).ToListAsync(ct)).ToLookup(v => v.PollId);
        var globalEvents = await db.GlobalEvents.AsNoTracking().Where(x => x.SeasonId == seasonId).ToListAsync(ct);
        var interactions = await db.Interactions.AsNoTracking().Where(x => x.SeasonId == seasonId).ToListAsync(ct);
        var requests = await db.PartnerRequests.AsNoTracking().Where(x => x.SeasonId == seasonId).ToListAsync(ct);
        var challenges = await db.WeeklyChallenges.AsNoTracking().Where(x => x.SeasonId == seasonId).ToListAsync(ct);
        var challengeIds = challenges.Select(c => c.Id).ToList();
        var completions = (await db.WeeklyChallengeCompletions.AsNoTracking().Where(c => challengeIds.Contains(c.ChallengeId)).ToListAsync(ct))
            .ToLookup(c => c.ChallengeId);
        var suggestions = await db.Suggestions.AsNoTracking().Where(x => x.SeasonId == seasonId).ToListAsync(ct);

        return state with
        {
            Polls = polls.ToImmutableSortedDictionary(
                p => p.Id,
                p => Json<Poll>(p.DataJson) with
                {
                    Status = p.Status,
                    StartedAt = p.StartedAt,
                    ClosesAt = p.ClosesAt,
                    Anonymous = p.Anonymous,
                    Options = [.. options[p.Id].OrderBy(o => o.Position).Select(o => new Engine.Polls.PollOption(o.OptionId, o.Label))],
                    Votes = [.. votes[p.Id].OrderBy(v => v.Position).Select(v => new Vote(v.UserId, v.OptionId))],
                }),
            GlobalEvents = globalEvents.ToImmutableSortedDictionary(
                x => x.Id,
                x => Json<GlobalEventState>(x.DataJson) with
                {
                    ObjectId = x.ObjectId,
                    Status = x.Status,
                    StartedAt = x.StartedAt,
                    EndsAt = x.EndsAt,
                    Reached = x.Reached,
                }),
            Interactions = interactions.ToImmutableSortedDictionary(
                x => x.Id,
                x => new Interaction(x.Id, x.Kind, x.FromPlayerId, x.ToPlayerId, x.RunId, x.CreatedAt, x.ExpiresAt, x.Status)),
            PartnerRequests = requests.ToImmutableSortedDictionary(
                x => x.Id,
                x => new PartnerRequest(x.Id, x.PlayerId, x.RunId, x.CreatedAt, x.ExpiresAt, x.Status)),
            WeeklyChallenges = challenges.ToImmutableSortedDictionary(
                x => x.Id,
                x => new WeeklyChallengeState(
                    x.Id,
                    Json<Engine.Content.WeeklyChallengeDefinition>(x.DefinitionJson),
                    x.StartsAt,
                    x.EndsAt,
                    x.RewardCoins,
                    [.. completions[x.Id].OrderBy(c => c.Position).Select(c => new WeeklyChallengeCompletion(c.PlayerId, c.RunId, c.Coins))])),
            Suggestions = suggestions.ToImmutableSortedDictionary(
                x => x.Id,
                x => new Suggestion(
                    x.Id, x.PlayerId, x.UserId, x.Kind, x.Text,
                    x.GameJson is null ? null : Json<Engine.Pool.GameCard>(x.GameJson),
                    x.SubmittedAt, x.Status, x.Comment)),
        };
    }

    // The partner's column wins over the JSON of his side: a partner changed behind the queue is a difference (L4)
    private static CoopPartner? CoopOf(RunRecord run) => run.PartnerId switch
    {
        null => null,
        { } partner when run.CoopJson is null => new CoopPartner(partner, Guid.Empty, default),
        { } partner => Json<CoopPartner>(run.CoopJson) with { PlayerId = partner },
    };

    private static T Json<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, EngineJson.Options) ?? throw new InvalidOperationException($"A stored {typeof(T).Name} is null.");

    private static IEnumerable<Guid> Changed<T>(ImmutableSortedDictionary<Guid, T> before, ImmutableSortedDictionary<Guid, T> after)
        where T : class =>
        after.Keys.Union(before.Keys).Where(id => !EqualityComparer<T?>.Default.Equals(before.GetValueOrDefault(id), after.GetValueOrDefault(id)));

    private static InvalidOperationException Missing(string what, Guid id) => new($"The {what} {id} has no projection row.");
}
