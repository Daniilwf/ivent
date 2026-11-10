using System.Text.Json;
using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Map;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;
using GameEvent.Engine.Undo;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Kernel;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Hosting;

/// <summary>
/// The demo season (`dotnet run -- seed-demo`, Development only; SPEC «Демо-сезон», F2, D-126): 16 players, the whole pool
/// of content/pool.demo.json and four weeks of play by bots — rolls, rerolls, runs of every difficulty, drops and tech
/// rerolls, proofs sent, approved and rejected, reviews, manual effects resolved and waiting, an inactive player, finishes,
/// an undo. The bots only send commands, so the log is what the engine made of them: valid by construction. The same seed
/// gives the same season; the site's clock is moved back four weeks for the play and set right after.
/// Stage 2 (D-270): the season plays on the graph map of content/map.demo.json — forks, a teleport forward and a snake,
/// checkpoints, points bonuses, zones. It starts without the shortcut zone «quick»; the admin publishes the whole map in
/// the middle of the season, while players stand on it. In the last days the players leave their branch choices
/// waiting, so the season ends with players standing at a fork.
/// </summary>
public static class DemoSeed
{
    public static readonly Guid SeasonId = Guid.Parse("de300000-0000-0000-0000-000000000001");

    public const int Seed = 20260926;
    public const int Days = 28;

    /// <summary>The day the admin publishes the whole map (D-270); the bots' branch choices are left waiting from <see cref="BranchesWaitFrom"/>.</summary>
    public const int MapPublishedOn = 13;

    public const int BranchesWaitFrom = Days - 2;

    /// <summary>The zone of the shortcut that the mid-season publication adds.</summary>
    public const string AddedZone = "quick";

    private static readonly string[] s_names =
    [
        "Лиса", "Барсук", "Ёж", "Сова", "Волк", "Рысь", "Енот", "Бобр",
        "Выдра", "Крот", "Ворон", "Филин", "Заяц", "Лось", "Тюлень", "Сокол",
    ];

    private static readonly Difficulty[] s_difficulties =
    [
        Difficulty.Easy, Difficulty.Easy, Difficulty.Normal, Difficulty.Normal, Difficulty.Normal, Difficulty.Normal,
        Difficulty.Hard, Difficulty.Hard, Difficulty.Hard, Difficulty.Extreme,
    ];

    private static readonly string?[] s_reviews =
    [
        "Отличная игра, не ожидал", "Затянуто, но финал хороший", "Прошёл с удовольствием", "Не моё",
        "Сложно, но честно", null, null,
    ];

    /// <summary>Builds the demo season once; true when it is there (built now or before).</summary>
    public static async Task<bool> RunAsync(IServiceProvider services, string contentRoot, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<GameEventDbContext>>();
        var bus = services.GetRequiredService<CommandBus>();
        var clock = services.GetRequiredService<IClock>() as IAdjustableClock
            ?? throw new InvalidOperationException("The demo season needs the movable clock of Development.");
        var random = services.GetRequiredService<IRandomSource>() as IReseedableRandom;
        var password = services.GetRequiredService<IConfiguration>()["DevSeed:Password"]
            ?? throw new InvalidOperationException("DevSeed:Password is not set (appsettings.Development.json).");

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            // The deadline is the bots' last command: a season without it was cut short and is not the demo season
            if (await db.Seasons.AnyAsync(s => s.Id == SeasonId, ct))
            {
                return await db.Events.AnyAsync(e => e.SeasonId == SeasonId && e.Type == "season-deadline-set", ct)
                    ? true
                    : throw new InvalidOperationException("The demo season was cut short: build it again with npm run seed:demo.");
            }

            // Not over another database: the demo accounts and the whole pool would land in it
            if (await db.Seasons.AnyAsync(ct))
            {
                throw new InvalidOperationException("This database has other seasons: the demo season goes into its own (npm run seed:demo).");
            }
        }

        var users = new List<(string, string, Role)> { ("admin", "Админ", Role.Admin), ("zritel", "Зритель", Role.Spectator) };
        users.AddRange(s_names.Select((name, i) => ($"player{i + 1:00}", name, Role.Player)));
        await DevSeed.SeedAccountsAsync(bus, factory, users, password, ct);
        await DevSeed.SeedPoolAsync(bus, factory, Path.Combine(contentRoot, "content", "pool.demo.json"), ct);

        // The play takes the days before today, mornings to evenings: every event is in the past when the clock is back
        var realNow = clock.UtcNow;
        clock.MoveTo(new DateTimeOffset(realNow.UtcDateTime.Date, TimeSpan.Zero).AddDays(-Days).AddHours(9));
        random?.Seed(Seed);
        try
        {
            await new Bots(factory, bus, clock, ct).PlayAsync(DemoMap(contentRoot));
        }
        finally
        {
            clock.Reset();
            random?.Seed(null);
        }

        await new Bots(factory, bus, clock, ct).FinishAsync(realNow.AddDays(5));
        return true;
    }

    /// <summary>
    /// The demo map (content/map.demo.json) as the admin publishes it mid-season, and the first version the season starts
    /// with: the same cells and arrows without the zone <see cref="AddedZone"/> and its teleport (an empty cell there).
    /// </summary>
    public static (MapGraph First, MapGraph Published) DemoMap(string contentRoot)
    {
        var published = ContentJson.Parse<MapGraph>(File.ReadAllText(Path.Combine(contentRoot, "content", "map.demo.json")));
        var first = published with
        {
            Cells = [.. published.Cells.Select(c => c.Zone != AddedZone ? c : c with { Zone = null, Type = c.Type == CellType.Teleport ? CellType.Empty : c.Type, To = null })],
            Zones = [.. published.Zones.Where(z => z.Id != AddedZone)],
        };
        return (first, published);
    }

    private sealed class Bots(IDbContextFactory<GameEventDbContext> factory, CommandBus bus, IAdjustableClock clock, CancellationToken ct)
    {
#pragma warning disable CA5394 // the bots' choices are seeded on purpose: the same demo season every time
        private readonly Random _random = new(Seed);

        private Guid _admin;
        private Guid _lastCommand;
        private bool _undone;

        /// <summary>The last command, after the play with the real clock back: the deadline, five days from now.</summary>
        public async Task FinishAsync(DateTimeOffset deadline)
        {
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                _admin = await db.Users.Where(u => u.Login == "admin").Select(u => u.Id).SingleAsync(ct);
            }

            await SendAsync(new SetSeasonDeadline(deadline), _admin);
        }

        private (MapGraph First, MapGraph Published) _map;
        private int _day;

        public async Task PlayAsync((MapGraph First, MapGraph Published) map)
        {
            _map = map;
            List<Guid> players;
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                _admin = await db.Users.Where(u => u.Login == "admin").Select(u => u.Id).SingleAsync(ct);
                players = await db.Users.Where(u => u.Role == Role.Player && u.Login.StartsWith("player"))
                    .OrderBy(u => u.Login)
                    .Select(u => u.Id)
                    .ToListAsync(ct);
            }

            var rules = RulesetJson.Default();
            rules = rules with { Features = rules.Features with { MapMode = MapMode.Graph } };
            await SendAsync(new CreateSeason(SeasonId, "Демо-сезон", rules, Map: _map.First), _admin);
            for (var i = 0; i < players.Count; i++)
            {
                await SendAsync(new AddSeasonPlayer(Guid.CreateVersion7(), players[i], s_names[i]), _admin);
            }

            await SendAsync(new ChangeSeasonStatus(SeasonStatus.Active), _admin);
            var quiet = players[^1];

            for (var day = 0; day < Days; day++)
            {
                _day = day;
                foreach (var userId in players.OrderBy(_ => _random.Next()))
                {
                    // One player goes quiet in the second week: the admin marks him inactive a few days later
                    if (userId == quiet && day >= 8)
                    {
                        continue;
                    }

                    clock.Advance(TimeSpan.FromMinutes(_random.Next(10, 50)));
                    await PlayerTurnAsync(userId);
                }

                clock.Advance(TimeSpan.FromMinutes(30));
                await AdminTurnAsync(day, quiet);
                var nextMorning = clock.UtcNow.UtcDateTime.Date.AddDays(1).AddHours(9);
                clock.MoveTo(new DateTimeOffset(nextMorning, TimeSpan.Zero));
            }
        }

        private async Task PlayerTurnAsync(Guid userId)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await db.SeasonPlayers.AsNoTracking().SingleOrDefaultAsync(p => p.SeasonId == SeasonId && p.UserId == userId, ct) is not { } player
                || player.IsInactive)
            {
                return;
            }

            // A finisher proves every run: the first place is final only when all its runs are approved (Q-3). An idle
            // player often sends the proofs they owe too, so the unchecked limit (D-134) does not stall them for the month.
            if (player.FinishOrder is not null || (player.Phase == TurnPhase.Idle && _random.NextDouble() < 0.6))
            {
                var sent = await db.Proofs.AsNoTracking().Where(p => p.SeasonId == SeasonId && p.PlayerId == player.Id && p.SubmittedAt != null).Select(p => p.RunId).ToListAsync(ct);
                var unproved = await db.Runs.AsNoTracking()
                    .Where(r => r.SeasonId == SeasonId && r.PlayerId == player.Id && r.Status == RunStatus.Completed && !sent.Contains(r.Id))
                    .Select(r => r.Id)
                    .ToListAsync(ct);
                foreach (var runId in unproved)
                {
                    await TryAsync(new SubmitProof(player.Id, runId, [$"https://imgur.com/a/demo{_random.Next(100000, 999999)}"], "Титры"), userId);
                }
            }

            // A branch left waiting (an undone choice) is chosen first; in the last days it stays for the screens to show
            if (await ChooseBranchesAsync(player.Id, userId, _day))
            {
                return;
            }

            switch (player.Phase)
            {
                case TurnPhase.Idle when _random.NextDouble() < 0.7:
                    await TryAsync(new RollGame(player.Id), userId);
                    break;

                case TurnPhase.Rolling:
                    if (_random.NextDouble() < 0.12 && await TryAsync(new Reroll(player.Id), userId))
                    {
                        break;
                    }

                    // Now and then the game does not launch at all: a tech reroll right after the start, well within its
                    // window (a later turn comes a day or two after the roll, when the window may be over)
                    if (await TryAsync(new StartRun(player.Id), userId) && _random.NextDouble() < 0.05)
                    {
                        await TryAsync(new TechReroll(player.Id, TechRerollReason.DoesNotLaunch, "Вылетает при запуске"), userId);
                    }

                    break;

                case TurnPhase.Playing:
                    var run = await db.Runs.AsNoTracking().Where(r => r.SeasonId == SeasonId && r.PlayerId == player.Id && r.Status == RunStatus.Playing).SingleOrDefaultAsync(ct);
                    if (run is null || clock.UtcNow - run.StartedAt < TimeSpan.FromHours(20))
                    {
                        break;
                    }

                    var roll = _random.NextDouble();
                    if (roll < 0.06)
                    {
                        await TryAsync(new DropRun(player.Id), userId);
                    }
                    else if (roll < 0.09)
                    {
                        await TryAsync(new TechReroll(player.Id, TechRerollReason.DoesNotLaunch, "Вылетает при запуске"), userId);
                    }
                    else if (roll < 0.55)
                    {
                        await CompleteAsync(player.Id, userId, run);
                    }

                    break;
            }
        }

        private async Task CompleteAsync(Guid playerId, Guid userId, Infrastructure.Seasons.RunRecord run)
        {
            using var snapshot = JsonDocument.Parse(run.SnapshotJson);
            var hoursKnown = snapshot.RootElement.TryGetProperty("hours", out var hours) && hours.ValueKind == JsonValueKind.Number;
            var difficulty = s_difficulties[_random.Next(s_difficulties.Length)];
            var review = _random.NextDouble() < 0.45 ? new RunReview(_random.Next(3, 11), s_reviews[_random.Next(s_reviews.Length)]) : null;
            var completed = await TryAsync(
                hoursKnown
                    ? new CompleteRun(playerId, difficulty, Review: review)
                    : new CompleteRun(playerId, difficulty, _random.Next(2, 26), "Оценка игрока", Review: review),
                userId);
            if (completed)
            {
                await ChooseBranchesAsync(playerId, userId, _day);
            }

            if (completed && _random.NextDouble() < 0.8)
            {
                await TryAsync(new SubmitProof(playerId, run.Id, [$"https://imgur.com/a/demo{_random.Next(100000, 999999)}"], "Титры и последний босс"), userId);
            }
        }

        /// <summary>
        /// The player's own move stopped at a fork: they pick a branch — the default one more often — and the move goes on;
        /// a second fork in the same throw asks again. From <see cref="BranchesWaitFrom"/> the choice is left waiting.
        /// True when a choice is waiting after it.
        /// </summary>
        private async Task<bool> ChooseBranchesAsync(Guid playerId, Guid userId, int day)
        {
            for (var asked = 0; asked < 4; asked++)
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var json = await db.SeasonPlayers.AsNoTracking().Where(p => p.Id == playerId).Select(p => p.ChoiceJson).SingleAsync(ct);
                if (json is null || JsonSerializer.Deserialize<PendingChoice>(json, EngineJson.Options) is not { Kind: ChoiceKind.Branch } choice)
                {
                    return false;
                }

                if (day >= BranchesWaitFrom)
                {
                    return true;
                }

                var pick = _random.NextDouble() < 0.6 ? choice.Options[0] : choice.Options[^1];
                await SendAsync(new MakeChoice(playerId, choice.ChoiceId, pick.Id), userId);
            }

            throw new InvalidOperationException("The demo season asked for a branch more times than the map has forks.");
        }

        private async Task AdminTurnAsync(int day, Guid quiet)
        {
            // Mid-season the admin publishes the whole map — the shortcut zone — while players stand on it (D-270)
            if (day == MapPublishedOn)
            {
                await SendAsync(new PublishMap(_map.Published, "Открыта «Зона быстрых игр» с коротким путём"), _admin);
            }

            await using var db = await factory.CreateDbContextAsync(ct);
            var day1 = clock.UtcNow.AddDays(-1);
            var proofs = await db.Proofs.AsNoTracking().Where(p => p.SeasonId == SeasonId && p.Status == ProofStatus.Pending && p.SubmittedAt != null).ToListAsync(ct);
            var finishers = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == SeasonId && p.FinishOrder != null).Select(p => p.Id).ToListAsync(ct);
            // The finishers' proofs are checked the same day: the first place freezes before the season ends. On the last
            // evening the admin checks none: the queue has proofs waiting for the screens to show
            foreach (var proof in proofs.Where(p => day < Days - 1 && (p.SubmittedAt <= day1 || finishers.Contains(p.PlayerId))))
            {
                var roll = _random.NextDouble();
                if (roll < 0.82 || finishers.Contains(proof.PlayerId))
                {
                    await TryAsync(new ApproveProof(proof.RunId), _admin);
                }
                else if (roll < 0.9)
                {
                    await TryAsync(new RejectProof(proof.RunId, "На скрине не видно титров"), _admin);
                }
            }

            // The last days' effects stay for the admin screen to show
            foreach (var effect in day < Days - 3 ? await db.ManualEffects.AsNoTracking().Where(e => e.SeasonId == SeasonId).ToListAsync(ct) : [])
            {
                if (_random.NextDouble() < 0.5)
                {
                    var applied = _random.NextDouble() < 0.7;
                    await TryAsync(new ResolveManualEffect(effect.Id, applied ? ManualEffectOutcome.Applied : ManualEffectOutcome.NotApplicable, applied ? null : "Ивент не подходит к игре", effect.PlayerId), _admin);
                }
            }

            if (day == 12)
            {
                var quietPlayer = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == SeasonId && p.UserId == quiet).Select(p => p.Id).SingleAsync(ct);
                await TryAsync(new SetPlayerInactive(quietPlayer, true), _admin);
            }

            if (day == 5)
            {
                var someone = await db.SeasonPlayers.AsNoTracking().Where(p => p.SeasonId == SeasonId).OrderBy(p => p.Name).Select(p => p.Id).FirstAsync(ct);
                await TryAsync(new AdjustPlayer(someone, "Компенсация за сбой сайта", PointsDelta: 3, CoinsDelta: 2), _admin);
            }

            // Once, the admin takes back the last command of the day: the log gets an undo
            if (day == 17 && !_undone && _lastCommand != Guid.Empty)
            {
                _undone = await TryAsync(new UndoCommand(_lastCommand, "Ошибся кнопкой"), _admin);
            }
        }

        private async Task<bool> TryAsync(ICommand command, Guid authorId)
        {
            var commandId = Guid.CreateVersion7();
            var outcome = await bus.SendAsync(new CommandEnvelope(commandId, SeasonId, command, authorId), ct);
            if (outcome.IsAccepted && command is not UndoCommand)
            {
                _lastCommand = commandId;
            }

            return outcome.IsAccepted;
        }

        private async Task SendAsync(ICommand command, Guid authorId)
        {
            if (!await TryAsync(command, authorId))
            {
                throw new InvalidOperationException($"The demo season could not start: {command.GetType().Name} was refused.");
            }
        }
#pragma warning restore CA5394
    }
}
