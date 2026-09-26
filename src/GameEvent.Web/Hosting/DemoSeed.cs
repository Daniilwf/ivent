using System.Text.Json;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
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
/// </summary>
public static class DemoSeed
{
    public static readonly Guid SeasonId = Guid.Parse("de300000-0000-0000-0000-000000000001");

    public const int Seed = 20260926;
    public const int Days = 28;

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

    private static readonly string[] s_reviews =
    [
        "Отличная игра, не ожидал", "Затянуто, но финал хороший", "Прошёл с удовольствием", "Не моё",
        "Сложно, но честно", null!, null!,
    ];

    public static async Task RunAsync(IServiceProvider services, string contentRoot, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<GameEventDbContext>>();
        var bus = services.GetRequiredService<CommandBus>();
        var clock = services.GetRequiredService<IClock>() as IAdjustableClock
            ?? throw new InvalidOperationException("The demo season needs the movable clock of Development.");
        (services.GetRequiredService<IRandomSource>() as IReseedableRandom)?.Seed(Seed);
        var password = services.GetRequiredService<IConfiguration>()["DevSeed:Password"]
            ?? throw new InvalidOperationException("DevSeed:Password is not set (appsettings.Development.json).");

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            if (await db.Seasons.AnyAsync(s => s.Id == SeasonId, ct))
            {
                return;
            }
        }

        var users = new List<(string, string, Role)> { ("admin", "Админ", Role.Admin), ("zritel", "Зритель", Role.Spectator) };
        users.AddRange(s_names.Select((name, i) => ($"player{i + 1:00}", name, Role.Player)));
        await DevSeed.SeedAccountsAsync(bus, factory, users, password, ct);
        await DevSeed.SeedPoolAsync(bus, factory, Path.Combine(contentRoot, "content", "pool.demo.json"), ct);

        var realNow = clock.UtcNow;
        clock.MoveTo(realNow.AddDays(-Days));
        try
        {
            await new Bots(factory, bus, clock, ct).PlayAsync(realNow);
        }
        finally
        {
            clock.Reset();
        }
    }

    private sealed class Bots(IDbContextFactory<GameEventDbContext> factory, CommandBus bus, IAdjustableClock clock, CancellationToken ct)
    {
#pragma warning disable CA5394 // the bots' choices are seeded on purpose: the same demo season every time
        private readonly Random _random = new(Seed);

        private Guid _admin;
        private Guid _lastCommand;
        private bool _undone;

        public async Task PlayAsync(DateTimeOffset realNow)
        {
            Dictionary<Guid, Guid> players;
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                _admin = await db.Users.Where(u => u.Login == "admin").Select(u => u.Id).SingleAsync(ct);
                players = await db.Users.Where(u => u.Role == Role.Player && u.Login.StartsWith("player"))
                    .OrderBy(u => u.Login)
                    .ToDictionaryAsync(u => u.Id, u => u.Id, ct);
            }

            await SendAsync(new CreateSeason(SeasonId, "Демо-сезон", RulesetJson.Default()), _admin);
            foreach (var userId in players.Keys)
            {
                var name = s_names[players.Keys.ToList().IndexOf(userId)];
                await SendAsync(new AddSeasonPlayer(Guid.CreateVersion7(), userId, name), _admin);
            }

            await SendAsync(new ChangeSeasonStatus(SeasonStatus.Active), _admin);
            var quiet = players.Keys.Last();

            for (var day = 0; day < Days; day++)
            {
                foreach (var userId in players.Keys.OrderBy(_ => _random.Next()))
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
                var nextMorning = clock.UtcNow.Date.AddDays(1).AddHours(9);
                clock.MoveTo(new DateTimeOffset(nextMorning, TimeSpan.Zero));
            }

            await SendAsync(new SetSeasonDeadline(realNow.AddDays(5)), _admin);
        }

        private async Task PlayerTurnAsync(Guid userId)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await db.SeasonPlayers.AsNoTracking().SingleOrDefaultAsync(p => p.SeasonId == SeasonId && p.UserId == userId, ct) is not { } player
                || player.IsInactive)
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

                    await TryAsync(new StartRun(player.Id), userId);
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
            if (completed && _random.NextDouble() < 0.8)
            {
                await TryAsync(new SubmitProof(playerId, run.Id, [$"https://imgur.com/a/demo{_random.Next(100000, 999999)}"], "Титры и последний босс"), userId);
            }
        }

        private async Task AdminTurnAsync(int day, Guid quiet)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var day1 = clock.UtcNow.AddDays(-1);
            var proofs = await db.Proofs.AsNoTracking().Where(p => p.SeasonId == SeasonId && p.Status == ProofStatus.Pending && p.SubmittedAt != null).ToListAsync(ct);
            foreach (var proof in proofs.Where(p => p.SubmittedAt <= day1))
            {
                var roll = _random.NextDouble();
                if (roll < 0.82)
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
