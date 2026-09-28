using FsCheck.Xunit;
using GameEvent.Engine.Content;
using GameEvent.Engine.Economy;
using GameEvent.Engine.Effects;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Proofs;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Scoring;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Invariants;

/// <summary>
/// EC8: random games of stage 4 — the turn, the admin's gifts and corrections of coins, items used on random targets with
/// their choices, the shop, bets, the clock with timers, undos, proofs and the flags switched off and on — with the
/// invariants after every command: the replay (1), points and coins as the sum of their changes (2), no purchases in debt
/// (15), the inventory limit of purchases, the chain limits (12), effects of others never touching a begun step (16), the
/// pledge equal to the open stakes and rejects taking the win back (17), a disabled mechanic writing nothing (19), and an
/// accepted undo equal to the log without the command (13).
/// </summary>
public partial class PlayerAdminInvariantTests
{
    private static readonly string[] s_economyPlayers = ["Вася", "Петя", "Коля"];

    [Property(MaxTest = 200)]
    public void Invariants_hold_with_items_the_shop_and_bets(int seed, byte[] script) =>
        PlayEconomy(seed, script, CheckEconomyInvariants);

    [Property(MaxTest = 50)]
    public void Same_seed_and_commands_give_the_same_log_with_items_the_shop_and_bets(int seed, byte[] script)
    {
        var first = PlayEconomy(seed, script);
        var second = PlayEconomy(seed, script);

        Assert.Equal(first.Log, second.Log);
        Assert.Equal(first.State, second.State);
    }

    [Fact]
    public void Economy_variant_reaches_items_the_shop_bets_timers_and_undos()
    {
        // The generator must reach what it adds: items used, including hostile ones and interceptions, purchases, won and
        // lost bets, rejects taking a win back, expired offers, accepted undos of economy commands, refusals in debt.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(string what) => counts[what] = counts.GetValueOrDefault(what) + 1;
        for (var seed = 0; seed < 150; seed++)
        {
            var x = (uint)seed + 7;
            var script = Enumerable.Range(0, 300).Select(_ => (byte)((x = (x * 1103515245) + 12345) >> 16)).ToArray();
            PlayEconomy(seed, script, (s, command, _, _) =>
            {
                foreach (var e in s.Last.IsAccepted ? s.Last.Events : [])
                {
                    Count(e switch
                    {
                        ItemUsed => "used",
                        HostileReceived => "hostile",
                        HostileIntercepted => "intercepted",
                        LotBought => "bought",
                        BetSettled { Status: BetStatus.Won } => "won",
                        BetSettled { Status: BetStatus.Lost } => "lost",
                        BetSettled { Status: BetStatus.Revoked } => "revoked",
                        ShopOfferExpired => "expired",
                        EffectTriggered => "triggered",
                        RunDiceModified => "dice",
                        _ => "other",
                    });
                }

                if (command is UndoCommand && s.Last.IsAccepted)
                {
                    Count("undone");
                }

                if (s.Last.Rejection?.Code == RejectionCodes.CoinsInDebt)
                {
                    Count("debt");
                }
            });
        }

        foreach (var what in new[] { "used", "hostile", "intercepted", "bought", "won", "lost", "revoked", "expired", "triggered", "dice", "undone", "debt" })
        {
            Assert.True(counts.GetValueOrDefault(what) > 0, $"The economy games never reached «{what}».");
        }
    }

    private static Scenario PlayEconomy(int seed, byte[] script, Action<Scenario, ICommand, SeasonState, int>? afterEach = null)
    {
        var s = EconomyScenario.New(seed)
            .WithCategory("Horror").WithCategory("RPG")
            .WithGame("Silent Hill", 6, "Horror").WithGame("Dead Space", 9, "Horror").WithGame("Amnesia", 3, "Horror")
            .WithGame("Gothic", 30, "RPG").WithGame("Risen", 12, "RPG").WithGame("Tetris", null, "RPG");
        s.WithPlayers(s_economyPlayers);
        var pack = EconomyScenario.RepositoryPack();
        s.WithContent(pack);
        var objects = pack.Objects.Where(o => o.Kind == ObjectKind.Item).Select(o => o.Id).Concat(["shield-effect", "talisman-effect"]).ToArray();
        var setup = s.History.Count;
        return s.Explained(s =>
        {
            foreach (var b in script)
            {
                var before = s.State;
                var logLength = s.Log.Count;
                var command = EconomyCommandFor(s, b, objects, setup);
                s.Act(command);
                afterEach?.Invoke(s, command, before, logLength);
            }
        });
    }

    private static ICommand EconomyCommandFor(Scenario s, byte b, string[] objects, int setup)
    {
        var name = s_economyPlayers[(b / 16) % 3];
        var player = s.PlayerId(name);
        var other = s.PlayerId(s_economyPlayers[((b / 16) + 1 + (b % 2)) % 3]);
        var variant = s.Log.Count;
        var me = s.State.Players[player];
        switch (b % 16)
        {
            case 0:
                return new RollGame(player);
            case 1:
                return new StartRun(player);
            case 2:
            case 3:
                return new CompleteRun(player, variant % 5 == 0 ? Difficulty.Hard : Difficulty.Normal, EstimatedHours: 4, HoursSource: "оценка");
            case 4:
                return new AdjustInventory(player, objects[(b + variant) % objects.Length], null, "подарок");
            case 5:
            case 6:
            case 7:
                var items = me.Wallet.Inventory.Where(o => o.Kind == ObjectKind.Item).ToList();
                if (items.Count == 0)
                {
                    return new AdjustInventory(player, objects[variant % objects.Length], null, "подарок");
                }

                var item = items[variant % items.Count];
                var definition = s.State.Catalog.Get(item.ObjectId);
                string[] choices = definition.Effect?.Actions.Any(a => a is RequestChoiceAction) == true ? [variant % 2 == 0 ? "Horror" : "RPG"] : [];
                return new UseItem(player, item.InstanceId, variant % 7 == 0 ? player : other, [.. choices]);
            case 8:
                return new RollShop(player);
            case 9:
                return new BuyLot(player, b % 3);
            case 10:
                return new PlaceBet(player, other, s.Ruleset.Bets.DeadlineOptionsDays[variant % 3], 1 + (variant % 12));
            case 11:
                s.Advance(TimeSpan.FromMinutes(20 * (1 + (b % 5))) + TimeSpan.FromHours(variant % 4 == 0 ? 20 : 0));
                return new FireTimers();
            case 12:
                // The game's own commands only: the setup (players, content) stays
                var played = s.History.Count - setup;
                return played > 0 ? new UndoCommand(s.History[^(1 + (variant % Math.Min(3, played)))].CommandId, "ошибка") : new FireTimers();
            case 13:
                var completed = s.State.Runs.Values.Where(r => r.Status == RunStatus.Completed && r.Proof?.Status is not ProofStatus.Approved).ToList();
                if (completed.Count == 0)
                {
                    return new Reroll(player);
                }

                var run = completed[variant % completed.Count];
                return variant % 3 == 0 ? new RejectProof(run.RunId, "нет титров") : new ApproveProof(run.RunId, null);
            case 14:
                return variant % 4 == 0
                    ? new ChangeRuleset(s.Ruleset with { Features = s.Ruleset.Features with { Bets = !s.Ruleset.Features.Bets, Shop = !s.Ruleset.Features.Shop } })
                    : new Reroll(player);
            default:
                return new AdjustPlayer(player, "монетки", CoinsDelta: (variant % 21) - 8);
        }
    }

    private static void CheckEconomyInvariants(Scenario s, ICommand command, SeasonState before, int logLengthBefore)
    {
        // 1: the state is the fold of the log
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));

        // 2: points, coins and resources are the sums of their changes in the commands that count
        foreach (var player in s.State.Players.Values)
        {
            var log = s.EffectiveLog;
            Assert.Equal(log.OfType<PointsChanged>().Where(e => e.PlayerId == player.PlayerId).Sum(e => e.Delta), player.Points);
            Assert.Equal(log.OfType<CoinsChanged>().Where(e => e.PlayerId == player.PlayerId).Sum(e => e.Delta), player.Coins);
        }

        CheckMechanics(s, command, before);
        CheckUndo(s, command, before);
        if (!s.Last.IsAccepted)
        {
            Assert.Equal(logLengthBefore, s.Log.Count);
            return;
        }

        var events = s.Last.Events;

        // 15: nothing is bought in debt, and a purchase never takes more than the player has
        var buyer = command switch
        {
            RollShop c => c.PlayerId,
            BuyLot c => c.PlayerId,
            PlaceBet c => c.PlayerId,
            _ => (Guid?)null,
        };
        if (buyer is { } id && events.Any(e => e is LotBought or BetPlaced || e is ShopRolled { Payment: ShopPayment.Coins }))
        {
            Assert.True(before.Players[id].Coins >= 0, "A purchase in debt.");
            Assert.True(s.State.Players[id].Coins >= 0, "A purchase took more coins than the player had.");
        }

        // Purchases respect the inventory limit
        if (command is BuyLot bought)
        {
            Assert.True(s.State.Players[bought.PlayerId].Wallet.Items <= s.State.Rules.Economy.InventoryLimit, "A purchase over the inventory limit.");
        }

        // 12: the reactions stay within the chain limits (the cut is the 51st event at most, lifetimes follow)
        var cut = events.OfType<EffectChainCut>().SingleOrDefault();
        Assert.True(cut is null || cut.Events <= Limits.MaxEventsPerCommand);

        // 16: an item used on another player never changes the roll or the throw they have begun
        if (command is UseItem use)
        {
            foreach (var target in before.Players.Values.Where(p => p.PlayerId != use.PlayerId))
            {
                var after = s.State.Players[target.PlayerId];
                Assert.Equal((target.Offer, target.Choice, target.Wallet.CurrentRoll), (after.Offer, after.Choice, after.Wallet.CurrentRoll));
                if (target.ActiveRunId is { } active)
                {
                    Assert.Equal(before.Runs[active], s.State.Runs[active]);
                }
            }
        }

        // 17: no bet on oneself; a revoked bet was won on a run rejected since; a run rejected leaves no bet won on it
        foreach (var player in s.State.Players.Values)
        {
            foreach (var bet in player.Wallet.Bets)
            {
                Assert.NotEqual(player.PlayerId, bet.OnPlayerId);
                if (bet.Status == BetStatus.Won)
                {
                    Assert.NotEqual(RunStatus.Rejected, s.State.Runs[bet.RunId].Status);
                }
            }
        }

        // 17: the pledge — stakes paid for bets still open — equals the open stakes
        var log2 = s.EffectiveLog;
        var placed = log2.OfType<BetPlaced>().Select(p => p.Bet.BetId).ToHashSet();
        var open = s.State.Players.Values.SelectMany(p => p.Wallet.Bets).Where(b => b.Status == BetStatus.Open).ToList();
        Assert.All(open, b => Assert.Contains(b.BetId, placed));
        Assert.Equal(
            open.Sum(b => b.Stake),
            log2.OfType<BetPlaced>().Where(p => open.Any(o => o.BetId == p.Bet.BetId)).Sum(p => p.Bet.Stake));
    }
}
