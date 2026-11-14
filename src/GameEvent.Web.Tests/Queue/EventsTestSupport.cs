using System.Data.Common;
using GameEvent.Engine.Content;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Tests.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameEvent.Web.Tests.Queue;

/// <summary>
/// Shared steps of the task 5.16 tests (migration «Events», the season's timer column, the projection of the stage 5
/// mechanics; D-727, D-756, D-810). Written by <c>test-author</c> from the SPEC «Модель данных» and the public contract of
/// the engine, before the implementation: the new tables and columns are read and written with plain SQL, so the tests
/// compile before the migration exists and fail on the missing schema.
/// </summary>
internal static class EventsTestSupport
{
    /// <summary>The tables the migration «Events» adds (SPEC «Модель данных», D-810).</summary>
    public static readonly string[] Tables =
        ["Poll", "PollOption", "Vote", "GlobalEvent", "Interaction", "PartnerRequest", "WeeklyChallenge", "WeeklyChallengeCompletion", "Suggestion"];

    /// <summary>The columns it adds to existing tables (D-810).</summary>
    public static readonly (string Table, string Column)[] Columns =
    [
        ("Run", "PartnerId"), ("Run", "ConditionsJson"), ("Run", "ConditionMet"), ("PendingManualEffect", "IrlProofJson"), ("Season", "NextTimerAt"),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<SeasonState> StateAsync(SiteFactory site)
    {
        await using var db = site.NewDb();
        var (state, _) = await EventLogReader.ReplaySeasonAsync(db, SiteFactory.SeasonId, Ct);
        return state;
    }

    /// <summary>The admin changes the season's rules by <paramref name="change"/> (all sections are in the default ruleset).</summary>
    public static async Task RulesAsync(SiteFactory site, Func<Ruleset, Ruleset> change)
    {
        var state = await StateAsync(site);
        await site.SendAsync(new ChangeRuleset(change(state.Rules)));
    }

    public static Func<Ruleset, Ruleset> Flags(Func<Features, Features> change) => r => r with { Features = change(r.Features) };

    /// <summary>Polls on (D-512), items with them: a poll whose result acts needs them (D-544).</summary>
    public static Task PollsOnAsync(SiteFactory site) => RulesAsync(site, Flags(f => f with { Items = true, Polls = true }));

    /// <summary>A nomination of 24 hours: no effect of its result, so the deadline does not cancel it (D-516).</summary>
    public static PollDefinition Nomination(bool anonymous = false, int hours = 24, bool? showLiveCounts = null) => new()
    {
        Question = "Лучший момент недели?",
        Options = new ChoiceOptionsSpec { List = ["Финиш", "Дроп", "Реролл"] },
        Voters = PollVoters.Players,
        Anonymous = anonymous,
        ClosesInHours = hours,
        ShowLiveCounts = showLiveCounts,
    };

    /// <summary>«Монетный дождь»: an event for everyone, +2 coins.</summary>
    public static readonly ObjectDefinition CoinRain = new()
    {
        Id = "coin-rain",
        Kind = ObjectKind.Event,
        Name = "Монетный дождь",
        Description = "Всем по 2 монетки.",
        Rarity = Rarity.Common,
        Effect = new EffectSpec
        {
            Target = new TargetSpec { Selector = TargetSelector.All },
            Actions = [new ChangeResourceAction { Resource = "coins", Amount = ContentValue.Of(2) }],
        },
    };

    /// <summary>Global events on with events and items (D-517); «Монетный дождь» published.</summary>
    public static async Task GlobalEventsOnAsync(SiteFactory site)
    {
        await RulesAsync(site, Flags(f => f with { Items = true, Events = true, GlobalEvents = true }));
        await site.SendAsync(new PublishContent(new ContentPack { Objects = [CoinRain] }, "Контент"));
    }

    /// <summary>Every game of the seeded pool becomes a coop game (pool commands through the queue), so any roll can be shared.</summary>
    public static async Task CoopPoolAsync(SiteFactory site)
    {
        List<GameRecord> games;
        await using (var db = site.NewDb())
        {
            games = await db.Games.AsNoTracking().ToListAsync(Ct);
        }

        var bus = site.Services.GetRequiredService<CommandBus>();
        foreach (var game in games)
        {
            var card = new GameCard(game.Title, PoolReader.Tags(game.TagsJson), game.Hours, null, null, null, null, IsCoop: true);
            var outcome = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, new ChangeGame(game.Id, card, Force: true), AuthorId: null), Ct);
            Assert.True(outcome.IsAccepted, outcome.Rejection?.ToString());
        }
    }

    /// <summary>Coop on (D-520) over a pool of coop games; the partner board too when asked (D-524).</summary>
    public static async Task CoopOnAsync(SiteFactory site, bool partnerBoard = false)
    {
        await CoopPoolAsync(site);
        await RulesAsync(site, Flags(f => f with { Coop = true, PartnerBoard = partnerBoard }));
    }

    /// <summary>The player rolls and starts a game.</summary>
    public static async Task<Guid> PlayingAsync(SiteFactory site, Guid player)
    {
        await site.SendAsync(new Engine.Rolls.RollGame(player));
        await site.SendAsync(new Engine.Runs.StartRun(player));
        return (await StateAsync(site)).Players[player].ActiveRunId!.Value;
    }

    /// <summary>Маша joins the seeded season as a third player.</summary>
    public static async Task<Guid> MashaAsync(SiteFactory site)
    {
        var masha = Guid.NewGuid();
        await site.SendAsync(new Engine.Players.AddSeasonPlayer(masha, site.Users["masha"], "masha"));
        site.Players["masha"] = masha;
        return masha;
    }

    public static async Task TickAsync(SiteFactory site)
    {
        var scheduler = site.Services.GetServices<IHostedService>().OfType<DeadlineScheduler>().Single();
        await scheduler.TickAsync(Ct);
    }

    public static async Task<int> CountAsync(SiteFactory site, string type)
    {
        await using var db = site.NewDb();
        return await db.Events.CountAsync(e => e.SeasonId == SiteFactory.SeasonId && e.Type == type, Ct);
    }

    public static async Task<IntegrityReport> IntegrityAsync(SiteFactory site)
    {
        await using var db = site.NewDb();
        return (await SeasonIntegrity.CheckAsync(db, SiteFactory.SeasonId, Ct))!;
    }

    public static async Task AssertIntactAsync(SiteFactory site)
    {
        var report = await IntegrityAsync(site);
        Assert.True(report.IsIntact, string.Join("; ", report.Differences));
    }

    // ---- Plain SQL: the schema of the migration «Events» is not in the model before it exists ----

    public static async Task<bool> TableExistsAsync(DbContext db, string table) =>
        Convert.ToInt64(await ScalarAsync(db, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $p0", table), System.Globalization.CultureInfo.InvariantCulture) == 1;

    public static async Task<IReadOnlyList<string>> ColumnsOfAsync(DbContext db, string table) =>
        [.. (await RowsAsync(db, "SELECT name FROM pragma_table_info($p0)", table)).Select(r => (string)r[0]!)];

    /// <summary>The table exists with every one of <paramref name="columns"/>: a clear failure before the migration «Events».</summary>
    public static async Task AssertSchemaAsync(DbContext db, string table, params string[] columns)
    {
        Assert.True(await TableExistsAsync(db, table), $"Table {table} is missing: the migration «Events» (5.16) adds it.");
        var have = await ColumnsOfAsync(db, table);
        foreach (var column in columns)
        {
            Assert.True(have.Contains(column, StringComparer.Ordinal), $"Column {table}.{column} is missing: the migration «Events» (5.16) adds it.");
        }
    }

    public static async Task<object?> ScalarAsync(DbContext db, string sql, params object?[] args)
    {
        await using var command = await CommandAsync(db, sql, args);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is DBNull ? null : value;
    }

    public static async Task<List<object?[]>> RowsAsync(DbContext db, string sql, params object?[] args)
    {
        await using var command = await CommandAsync(db, sql, args);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(Ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = await reader.IsDBNullAsync(i, Ct) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public static async Task<int> ExecuteAsync(DbContext db, string sql, params object?[] args)
    {
        await using var command = await CommandAsync(db, sql, args);
        return await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<DbCommand> CommandAsync(DbContext db, string sql, object?[] args)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(Ct);
        }

        var command = connection.CreateCommand();
#pragma warning disable CA2100 // test-only SQL with parameters
        command.CommandText = sql;
#pragma warning restore CA2100
        for (var i = 0; i < args.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"$p{i}";
            parameter.Value = args[i] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
