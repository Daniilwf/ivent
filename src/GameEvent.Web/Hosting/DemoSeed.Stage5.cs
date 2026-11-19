using GameEvent.Engine.Content;
using GameEvent.Engine.Effects;
using GameEvent.Engine.GlobalEvents;
using GameEvent.Engine.Interactions;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Polls;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Hosting;

public static partial class DemoSeed
{
    private sealed class Stage5Demo(
        IDbContextFactory<GameEventDbContext> factory,
        CommandBus bus,
        SeasonStateReader reader,
        Guid admin,
        CancellationToken ct)
    {
        private const string Karaoke = "karaoke";
        private const string Tailwind = "tailwind";
        private const string Trial = "demo-hard-mode";
        private const string Kit = "demo-event-kit";
        private const string TrialDeck = "demo-trial-deck";
        private const string IrlDeck = "demo-irl-deck";
        private const string TailwindDeck = "demo-tailwind-deck";
        private const string SeedComment = "Подготовка демо-сезона";
        private const string ShortGames = "Короткие игры";
        private const string LongGames = "Большие приключения";

        private static readonly string[] s_coopTitles = ["It Takes Two", "Cuphead", "Portal 2", "A way out", "Borderlands 2", "trine 2", "Human Fall Flat"];

        public async Task PlayAsync(string contentRoot)
        {
            var initial = await ReadAsync();
            var features = initial.Rules.Features with { Events = true, Polls = true, Coop = true, GlobalEvents = true };
            await RequireAsync(new ChangeRuleset(initial.Rules with { Features = features }), admin);

            var (roller, partner) = await PreparePairAsync(initial);
            var games = await AvailableCoopGamesAsync(await ReadAsync(), roller, partner);
            var pack = DevSeed.Pack(contentRoot);
            await RequireAsync(new PublishContent(pack with
            {
                Objects = [.. pack.Objects, TrialDefinition(), EventKit(), CoopRoll(games[0], 1), CoopRoll(games[1], 2)],
                Wheels = [.. pack.Wheels, Deck(TrialDeck, "Испытание", Trial, EventKind.Bad),
                    Deck(IrlDeck, "Караоке", Karaoke, EventKind.Special),
                    Deck(TailwindDeck, "Попутный ветер", Tailwind, EventKind.Good)],
            }, "Колоды и ивенты демо-сезона"), admin);

            await SeedPollsAsync(roller, partner);
            var goal = await LaunchGoalAsync(1);
            var completed = await StartCoopAsync(roller, partner, games[0], 1);
            await RequireAsync(new CompleteRun(roller.PlayerId, Difficulty.Normal,
                EstimatedHours: completed.Snapshot.Hours is null ? 6 : null,
                HoursSource: completed.Snapshot.Hours is null ? "Оценка демо-прохождения" : null), roller.UserId);
            await FollowBranchesAsync(roller);
            await FollowBranchesAsync(partner);
            await RequireAsync(new SubmitProof(roller.PlayerId, completed.RunId,
                ["https://example.invalid/demo/coop"], "Титры после кооп-прохождения"), roller.UserId);
            await RequireAsync(new ApproveProof(completed.RunId), admin);
            await RequireAsync(new EndGlobalEvent(goal), admin);

            await LaunchGoalAsync(12);
            await StartCoopAsync(roller, partner, games[1], 2);
            await SeedIrlProofsAsync();
        }

        private async Task<(SeasonPlayer Roller, SeasonPlayer Partner)> PreparePairAsync(SeasonState state)
        {
            var candidates = state.Players.Values.Where(p => !p.IsInactive && p.Finish?.Order != 1
                && p.ActiveRunId is null && p.Choice is null && (p.Phase == TurnPhase.Idle || p.Offer is not null))
                .OrderBy(p => p.Offer is not null).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
            var roller = candidates.First(p => p.Finish is null && p.Wallet.Items < state.Rules.Economy.InventoryLimit
                && !UncheckedRuns.ClosesRoll(state, p.PlayerId) && p.Wallet.NextRoll.All(m => m.Filter is null));
            var partner = candidates.First(p => p.PlayerId != roller.PlayerId);
            foreach (var player in new[] { roller, partner }.Where(p => p.Offer is not null))
            {
                await RequireAsync(new AdjustPlayer(player.PlayerId, "Освободить игрока для демо-коопа", DiscardOffer: true), admin);
            }

            return (roller, partner);
        }

        private async Task<List<GameRecord>> AvailableCoopGamesAsync(SeasonState state, SeasonPlayer roller, SeasonPlayer partner)
        {
            var reserved = state.Runs.Values.Where(r => r.Status is RunStatus.Playing or RunStatus.Completed).Select(r => r.GameId)
                .Concat(state.Players.Values.Select(p => p.Offer?.GameId).OfType<Guid>())
                .Concat(state.Players.Values.SelectMany(p => p.Choice?.Options ?? []).Select(o => o.Game?.GameId).OfType<Guid>())
                .Concat(roller.Exclusions.Select(e => e.GameId)).Concat(partner.Exclusions.Select(e => e.GameId)).ToHashSet();
            await using var db = await factory.CreateDbContextAsync(ct);
            var matching = await db.Games.AsNoTracking().Where(g => s_coopTitles.Contains(g.Title) && !g.IsDeleted).ToListAsync(ct);
            var games = matching.Where(g => !reserved.Contains(g.Id)).OrderBy(g => Array.IndexOf(s_coopTitles, g.Title)).Take(2).ToList();
            if (games.Count != 2)
            {
                throw new InvalidOperationException("The demo needs two available coop games after the bot history.");
            }

            foreach (var game in games.Where(g => !g.IsCoop))
            {
                var card = new GameCard(game.Title, PoolReader.Tags(game.TagsJson), game.Hours, game.Year,
                    game.SteamAppId, game.CoverFileId, game.Note, IsCoop: true, CompletionCondition: game.CompletionCondition);
                await RequireAsync(new ChangeGame(game.Id, card, Force: false), admin);
            }

            return games;
        }

        private async Task<RunStarted> StartCoopAsync(SeasonPlayer roller, SeasonPlayer partner, GameRecord game, int slot)
        {
            var state = await ReadAsync();
            if (state.Players[roller.PlayerId].CellId != state.Map.Start.Id)
            {
                await RequireAsync(new AdjustPlayer(roller.PlayerId, "Начало маршрута для демо-коопа", CellId: state.Map.Start.Id), admin);
            }

            await UseGiftAsync(roller, CoopItemId(slot));
            await RequireAsync(new RollGame(roller.PlayerId), roller.UserId);
            var run = await RequireEventAsync<RunStarted>(new StartRun(roller.PlayerId, GameId: game.Id), roller.UserId);
            var invite = await RequireEventAsync<InteractionCreated>(new InviteToCoop(roller.PlayerId, partner.PlayerId), roller.UserId);
            await RequireAsync(new AnswerInvitation(partner.PlayerId, invite.Interaction.InteractionId, Accept: true), partner.UserId);
            return run;
        }

        private async Task FollowBranchesAsync(SeasonPlayer player)
        {
            var state = await ReadAsync();
            while (state.Players[player.PlayerId].Choice is { Kind: ChoiceKind.Branch } choice)
            {
                await RequireAsync(new MakeChoice(player.PlayerId, choice.ChoiceId, choice.Options[0].Id), player.UserId);
                state = await ReadAsync();
            }
        }

        private async Task SeedPollsAsync(SeasonPlayer first, SeasonPlayer second)
        {
            var adjudicated = await StartPollAsync("Какой темп выбираем на эту неделю?");
            await RequireAsync(new CastVote(adjudicated, first.UserId, ShortGames), first.UserId);
            await RequireAsync(new CastVote(adjudicated, second.UserId, ShortGames), second.UserId);
            await RequireAsync(new ClosePoll(adjudicated), admin);

            var open = await StartPollAsync("Что сыграем в финале сезона?");
            await RequireAsync(new CastVote(open, first.UserId, ShortGames), first.UserId);
            await RequireAsync(new CastVote(open, second.UserId, LongGames), second.UserId);
        }

        private async Task<Guid> StartPollAsync(string question) =>
            (await RequireEventAsync<PollStarted>(new StartPoll(new PollDefinition
            {
                Question = question,
                Options = new ChoiceOptionsSpec { List = [ShortGames, LongGames] },
                Voters = PollVoters.Players,
                ShowLiveCounts = true,
                ClosesInHours = 24,
            }), admin)).PollId;

        private async Task<Guid> LaunchGoalAsync(int target) =>
            (await RequireEventAsync<GlobalEventLaunched>(new LaunchGlobalEvent(Tailwind, 24,
                new GlobalEventGoal { Metric = GoalMetric.Runs, Target = target }), admin)).GlobalEventId;

        private async Task SeedIrlProofsAsync()
        {
            var state = await ReadAsync();
            var players = state.Players.Values.Where(p => !p.IsInactive && p.Finish?.Order != 1
                && p.Choice?.Kind != ChoiceKind.Branch && p.Wallet.Items < state.Rules.Economy.InventoryLimit)
                .OrderBy(p => p.Name, StringComparer.Ordinal).Take(3).ToList();
            if (players.Count != 3)
            {
                throw new InvalidOperationException("The demo needs three players who can draw and prove an IRL event.");
            }

            await SubmitIrlAsync(players[0]);
            var approved = await SubmitIrlAsync(players[1]);
            await RequireAsync(new ReviewIrlProof(approved, Approved: true), admin);
            var rejected = await SubmitIrlAsync(players[2]);
            await RequireAsync(new ReviewIrlProof(rejected, Approved: false, Comment: "Не слышно исполнения, нужна запись со звуком"), admin);
        }

        private async Task<Guid> SubmitIrlAsync(SeasonPlayer player)
        {
            var effects = await UseGiftAsync(player, Kit);
            var id = effects.Select(e => e.Event).OfType<ManualEffectCreated>().Single(e => e.ObjectId == Karaoke).EffectId;
            await RequireAsync(new SubmitIrlProof(player.PlayerId, id, [], "https://example.invalid/demo/karaoke"), player.UserId);
            return id;
        }

        private async Task<IReadOnlyList<LoggedEvent>> UseGiftAsync(SeasonPlayer player, string objectId)
        {
            var gift = await RequireEventAsync<ObjectGiven>(new AdjustInventory(player.PlayerId, objectId, null, SeedComment), admin);
            return await RequireAsync(new UseItem(player.PlayerId, gift.Item.InstanceId), player.UserId);
        }

        private async Task<SeasonState> ReadAsync()
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await reader.ReadAsync(db, SeasonId, ct);
        }

        private async Task<T> RequireEventAsync<T>(ICommand command, Guid author) where T : IGameEvent =>
            (await RequireAsync(command, author)).Select(e => e.Event).OfType<T>().Single();

        private async Task<IReadOnlyList<LoggedEvent>> RequireAsync(ICommand command, Guid author)
        {
            var outcome = await bus.SendAsync(new CommandEnvelope(Guid.CreateVersion7(), command is IGlobalCommand ? Guid.Empty : SeasonId, command, author), ct);
            return outcome.IsAccepted ? outcome.Events : throw new InvalidOperationException($"The demo command {command.GetType().Name} was refused: {outcome.Rejection}.");
        }

        private static string CoopItemId(int slot) => $"demo-coop-{slot}";

        private static ObjectDefinition CoopRoll(GameRecord game, int slot) => new()
        {
            Id = CoopItemId(slot),
            Kind = ObjectKind.Item,
            Name = "Кооп-билет",
            Description = $"Следующим роллом выпадет {game.Title}.",
            Rarity = Rarity.Common,
            Price = 1,
            Window = UseWindow.BeforeRoll,
            Effect = new EffectSpec
            {
                Target = new TargetSpec { Selector = TargetSelector.Self },
                Actions = [new ModifyNextRollAction { Filter = new GameFilterSpec { Games = [game.Id.ToString("N")] } }],
            },
        };

        private static ObjectDefinition TrialDefinition() => new()
        {
            Id = Trial,
            Kind = ObjectKind.Event,
            Name = "Испытание на харде",
            Description = "Следующую игру нужно пройти на сложной сложности или выше.",
            Rarity = Rarity.Common,
            Effect = new EffectSpec
            {
                Target = new TargetSpec { Selector = TargetSelector.Self },
                Actions = [new ModifyNextRollAction { RunCondition = new ConditionSpec { DifficultyAtLeast = Difficulty.Hard } }],
            },
        };

        private static ObjectDefinition EventKit() => new()
        {
            Id = Kit,
            Kind = ObjectKind.Item,
            Name = "Набор ивентов",
            Description = "Разыграй три ивента, в том числе IRL-задание.",
            Rarity = Rarity.Common,
            Price = 1,
            Window = UseWindow.Anytime,
            Effect = new EffectSpec
            {
                Target = new TargetSpec { Selector = TargetSelector.Self },
                Actions = [new DrawEventAction { Deck = TrialDeck }, new DrawEventAction { Deck = TailwindDeck }, new DrawEventAction { Deck = IrlDeck }],
            },
        };

        private static WheelDefinition Deck(string id, string name, string objectId, EventKind kind) => new()
        {
            Id = id,
            Name = name,
            Deck = kind,
            Entries = [new WheelEntrySpec { ObjectId = objectId, Weight = 1 }],
        };
    }
}
