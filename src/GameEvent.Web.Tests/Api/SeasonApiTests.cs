using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rolls;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Turns;
using GameEvent.Web.Seasons;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Roll, start and complete over HTTP. Each endpoint: success, another role or player refused, invalid input
/// (CLAUDE.md «Тесты»). The player always acts as themselves: the player id comes from the session.
/// </summary>
public sealed class SeasonApiTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    // ---- Success ----

    [Fact]
    public async Task Player_rolls_starts_and_completes_through_the_api()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var roll = await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        var start = await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });

        // While playing, the season view shows the active run with its game
        var playing = await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct);
        Assert.Equal(TurnPhase.Playing, playing!.Me!.Phase);
        Assert.Null(playing.Me.Offer);
        Assert.Contains(playing.Me.ActiveRun!.Game.Title, new[] { "Silent Hill", "Alan Wake", "Outlast" });
        Assert.NotNull(playing.Me.ActiveRun.Game.Hours);
        Assert.Equal(_site.Clock.UtcNow, playing.Me.ActiveRun.StartedAt);

        var complete = await PostAsync(vasya, "complete", new { commandId = Guid.NewGuid(), difficulty = "hard" });

        Assert.Equal(["game-rolled"], await TypesAsync(roll));
        Assert.Equal(["run-started"], await TypesAsync(start));
        Assert.Equal(["run-completed", "completion-rolled", "points-changed", "player-moved", "coins-changed"], await TypesAsync(complete)); // Q-2, D-96: coins for the completion

        var season = await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct);
        var me = season!.Players.Single(p => p.Id == _site.Players["vasya"]);
        Assert.True(me.Points > 0);
        var last = season.Me!.LastCompleted!;
        Assert.Equal(me.Points, last.Total);
        Assert.Equal(last.Total, last.Dice.Sum(d => d.Value));
        Assert.All(last.Dice, d => Assert.Equal(6, d.Sides)); // hard
        Assert.Equal(Engine.Runs.Difficulty.Hard, last.Difficulty);
        Assert.NotEqual("start", me.CellId);
        Assert.Equal(TurnPhase.Idle, season.Me!.Phase);
        Assert.Equal(0, season.Players.Single(p => p.Id == _site.Players["petya"]).Points);
    }

    [Fact]
    public async Task Current_season_is_the_latest_one_the_player_plays_in_and_the_latest_for_others()
    {
        // Given a later season without Вася
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(1);
        var later = await _site.CreateSeasonAsync();

        // Then Вася keeps his season, a spectator and a player outside both see the latest one
        Assert.Equal(SiteFactory.SeasonId, await CurrentAsync("vasya"));
        Assert.Equal(later, await CurrentAsync("zritel"));
        Assert.Equal(later, await CurrentAsync("masha"));
    }

    [Fact]
    public async Task Current_season_needs_a_session()
    {
        var client = await _site.AnonymousAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/seasons/current", Ct)).StatusCode);
    }

    private async Task<Guid> CurrentAsync(string login)
    {
        var client = await _site.SignedInAsync(login);
        return (await client.GetFromJsonAsync<CurrentSeasonView>("/api/seasons/current", s_json, Ct))!.Id;
    }

    [Fact]
    public async Task Rolled_game_is_shown_to_the_player()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });

        var season = await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct);

        Assert.Equal(TurnPhase.Rolling, season!.Me!.Phase);
        Assert.Contains(season.Me.Offer!.Title, new[] { "Silent Hill", "Alan Wake", "Outlast" });
    }

    [Fact]
    public async Task Same_command_id_twice_acts_once()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var commandId = Guid.NewGuid();

        var first = await PostAsync(vasya, "roll", new { commandId });
        var second = await PostAsync(vasya, "roll", new { commandId });

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var repeat = await second.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct);
        Assert.True(repeat!.Duplicate);
        Assert.Equal((await first.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct))!.Events, repeat.Events);
    }

    [Fact]
    public async Task Spectator_and_outsider_can_view_the_season()
    {
        foreach (var login in new[] { "zritel", "masha", "admin" })
        {
            var client = await _site.SignedInAsync(login);

            var season = await client.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct);

            Assert.Equal(2, season!.Players.Count);
            Assert.Null(season.Me);
            Assert.Equal(Engine.Rulesets.RulesetJson.Default().Map.LinearLength + 1, season.Cells.Count);
        }
    }

    [Fact]
    public async Task Player_chooses_one_of_several_rolled_games_and_the_choice_survives_a_reload()
    {
        // Given a season where the wheel offers a choice of three games (D-91)
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(rules with { Roll = rules.Roll with { ChoiceCount = 3 } }, ExpectedVersion: null));
        var vasya = await _site.SignedInAsync("vasya");

        var roll = await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });

        // The pending choice is in the season view: a reloaded page shows the same options
        Assert.Equal(["game-choice-rolled"], await TypesAsync(roll));
        var rolled = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!;
        Assert.Equal(TurnPhase.Rolling, rolled.Phase);
        Assert.Null(rolled.Offer);
        var choice = rolled.Choice!;
        Assert.Equal(ChoiceKind.Game, choice.Kind);
        Assert.Equal(["Alan Wake", "Outlast", "Silent Hill"], choice.Options.Select(o => o.Game!.Title).Order());
        Assert.All(choice.Options, o => Assert.Equal(o.Game!.Id.ToString("N"), o.Id));

        // When the player picks one, it starts at once (Choosing --> Playing, D-91)
        var picked = choice.Options[1];
        var choose = await PostAsync(vasya, "choose", new { commandId = Guid.NewGuid(), choiceId = choice.Id, optionId = picked.Id });

        Assert.Equal(["choice-made", "run-started"], await TypesAsync(choose));
        var chosen = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!;
        Assert.Equal(TurnPhase.Playing, chosen.Phase);
        Assert.Null(chosen.Choice);
        Assert.Null(chosen.Offer);
        var playedGame = chosen.ActiveRun!.Game;
        Assert.Equal((picked.Game!.Id, picked.Game.Title, picked.Game.Hours), (playedGame.Id, playedGame.Title, playedGame.Hours));

        // A second tab answering the same choice is refused with the engine code
        var again = await vasya.PostAsJsonAsync(Url("choose"), new { commandId = Guid.NewGuid(), choiceId = choice.Id, optionId = picked.Id }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        using var problem = JsonDocument.Parse(await again.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.noPendingChoice", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reloading_the_page_after_a_roll_shows_the_same_offer()
    {
        // G5: the wheel is only an animation; the server decided at the click, a reload changes nothing
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });

        var first = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Offer!;
        var again = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Offer!;
        var otherTab = await _site.SignedInAsync("vasya");
        var fromOtherTab = (await otherTab.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Offer!;

        Assert.Equivalent(first, again, strict: true); // records with a list of marks: compared by value
        Assert.Equivalent(first, fromOtherTab, strict: true);
        await using var db = _site.NewDb();
        Assert.Equal(1, db.Events.Count(e => e.Type == "game-rolled"));
    }

    // ---- «Уже проходил» (G8, D-92) ----

    [Fact]
    public async Task Player_declares_already_played_and_gets_another_game_at_once()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        var offered = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Offer!;

        var response = await PostAsync(vasya, "already-played", new { commandId = Guid.NewGuid(), gameId = offered.Id });

        Assert.Equal(["game-excluded", "game-rolled"], await TypesAsync(response));
        var me = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!;
        Assert.Equal(TurnPhase.Rolling, me.Phase);
        Assert.NotEqual(offered.Id, me.Offer!.Id);
        await using var db = _site.NewDb();
        var row = Assert.Single(db.Exclusions.ToList());
        Assert.Equal((_site.Players["vasya"], offered.Id), (row.PlayerId, row.GameId));
    }

    [Fact]
    public async Task Repeating_already_played_with_the_same_command_id_acts_once()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        var offered = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Offer!;
        var body = new { commandId = Guid.NewGuid(), gameId = offered.Id };

        var first = await vasya.PostAsJsonAsync(Url("already-played"), body, Ct);
        var second = await vasya.PostAsJsonAsync(Url("already-played"), body, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True((await second.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct))!.Duplicate);
        await using var db = _site.NewDb();
        Assert.Equal(1, db.Events.Count(e => e.Type == "game-excluded"));
    }

    [Fact]
    public async Task Already_played_on_a_game_that_is_not_offered_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });

        var response = await vasya.PostAsJsonAsync(Url("already-played"), new { commandId = Guid.NewGuid(), gameId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("roll.gameNotOffered", problem.RootElement.GetProperty("code").GetString());
        await using var db = _site.NewDb();
        Assert.Empty(db.Exclusions.ToList());
    }

    [Fact]
    public async Task Already_played_before_rolling_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(Url("already-played"), new { commandId = Guid.NewGuid(), gameId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.wrongPhase", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_player_cannot_declare_another_players_offer()
    {
        // Petya names Vasya's offered game: the engine checks it against Petya's own turn
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        var offered = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Offer!;
        var petya = await _site.SignedInAsync("petya");
        await PostAsync(petya, "roll", new { commandId = Guid.NewGuid() });

        var response = await petya.PostAsJsonAsync(Url("already-played"), new { commandId = Guid.NewGuid(), gameId = offered.Id, playerId = _site.Players["vasya"] }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("roll.gameNotOffered", problem.RootElement.GetProperty("code").GetString());
        var mine = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Offer!;
        Assert.Equivalent(offered, mine, strict: true);
    }

    // ---- Reroll (RR1, D-93) ----

    [Fact]
    public async Task Player_rerolls_the_offer_and_gets_another_game_at_once()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        var offered = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Offer!;

        // The first reroll after a roll is free: no payment event
        var response = await PostAsync(vasya, "reroll", new { commandId = Guid.NewGuid() });

        Assert.Equal(["game-rerolled", "game-rolled"], await TypesAsync(response));
        var me = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!;
        Assert.Equal(TurnPhase.Rolling, me.Phase);
        Assert.NotEqual(offered.Id, me.Offer!.Id);
        await using var db = _site.NewDb();
        Assert.Equal(1, db.SeasonPlayers.Single(p => p.Id == _site.Players["vasya"]).RerollsThisRoll);
    }

    [Fact]
    public async Task Paid_reroll_through_the_api_spends_coins()
    {
        // Given Вася has exactly the price of one paid reroll (ruleset default: 5 coins)
        var price = RulesetJson.Default().Roll.RerollCost.Amount!.Value;
        await _site.SendAsync(new AdjustPlayer(_site.Players["vasya"], "Приз", CoinsDelta: price));
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "reroll", new { commandId = Guid.NewGuid() });

        var paid = await PostAsync(vasya, "reroll", new { commandId = Guid.NewGuid() });

        Assert.Equal(["game-rerolled", "coins-changed", "game-rolled"], await TypesAsync(paid));
        await using var db = _site.NewDb();
        Assert.Equal(0, db.SeasonPlayers.Single(p => p.Id == _site.Players["vasya"]).Coins);
    }

    [Fact]
    public async Task Reroll_without_coins_is_a_conflict_with_not_enough_coins()
    {
        // Given Вася used his free reroll and has no coins
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "reroll", new { commandId = Guid.NewGuid() });
        var offered = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Offer!;

        var response = await vasya.PostAsJsonAsync(Url("reroll"), new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("roll.notEnoughCoins", problem.RootElement.GetProperty("code").GetString());
        var me = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!;
        Assert.Equivalent(offered, me.Offer, strict: true);
        await using var db = _site.NewDb();
        Assert.Equal(1, db.Events.Count(e => e.Type == "game-rerolled"));
        Assert.Equal(0, db.SeasonPlayers.Single(p => p.Id == _site.Players["vasya"]).Coins);
    }

    [Fact]
    public async Task Reroll_before_rolling_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(Url("reroll"), new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.wrongPhase", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Repeating_a_reroll_with_the_same_command_id_acts_once()
    {
        // A repeated request of one click must not reroll twice (and so must not pay for a second reroll)
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        var body = new { commandId = Guid.NewGuid() };

        var first = await vasya.PostAsJsonAsync(Url("reroll"), body, Ct);
        var second = await vasya.PostAsJsonAsync(Url("reroll"), body, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var repeat = await second.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct);
        Assert.True(repeat!.Duplicate);
        Assert.Equal((await first.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct))!.Events, repeat.Events);
        await using var db = _site.NewDb();
        Assert.Equal(1, db.Events.Count(e => e.Type == "game-rerolled"));
    }

    [Fact]
    public async Task Player_rerolls_a_pending_choice_as_a_whole()
    {
        // Given a choice of two of the three games: after giving both up one game is left, a plain offer
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(rules with { Roll = rules.Roll with { ChoiceCount = 2 } }, ExpectedVersion: null));
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        var choice = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Choice!;

        var response = await PostAsync(vasya, "reroll", new { commandId = Guid.NewGuid() });

        Assert.Equal(["game-rerolled", "game-rolled"], await TypesAsync(response));
        var me = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!;
        Assert.Null(me.Choice);
        Assert.DoesNotContain(me.Offer!.Id, choice.Options.Select(o => o.Game!.Id));
    }

    [Fact]
    public async Task A_player_cannot_reroll_for_another_player()
    {
        // Extra fields naming another player are ignored: the session decides whose offer is given up
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        var offered = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Offer!;
        var petya = await _site.SignedInAsync("petya");

        var response = await petya.PostAsJsonAsync(Url("reroll"), new { commandId = Guid.NewGuid(), playerId = _site.Players["vasya"] }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.wrongPhase", problem.RootElement.GetProperty("code").GetString());
        var mine = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Offer!;
        Assert.Equivalent(offered, mine, strict: true);
    }

    // ---- The price of the next reroll and pending manual effects in the season view (D-93) ----

    /// <summary>The raw JSON of the player's own turn, as the frontend reads it.</summary>
    private static async Task<JsonElement> MeJsonAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.GetProperty("me").Clone();
    }

    private static void AssertNextReroll(JsonElement me, string payment, int coins)
    {
        var next = me.GetProperty("nextReroll");
        Assert.Equal(JsonValueKind.Object, next.ValueKind);
        Assert.Equal(payment, next.GetProperty("payment").GetString());
        Assert.Equal(coins, next.GetProperty("coins").GetInt32());
    }

    [Fact]
    public async Task Next_reroll_price_follows_the_payment_order_while_rolling()
    {
        var price = RulesetJson.Default().Roll.RerollCost.Amount!.Value;
        var vasya = await _site.SignedInAsync("vasya");

        // Idle: no price
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("nextReroll").ValueKind);

        // After a roll the first reroll is free
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        AssertNextReroll(await MeJsonAsync(vasya), "freeThisRoll", 0);

        // After the free one: the ruleset price in coins (even with 0 coins the screen shows the price)
        await PostAsync(vasya, "reroll", new { commandId = Guid.NewGuid() });
        AssertNextReroll(await MeJsonAsync(vasya), "coins", price);

        // A reroll coupon given by the admin comes before coins
        await _site.SendAsync(new AdjustPlayer(
            _site.Players["vasya"], "Купон", ResourceDeltas: [new ResourceDelta(RerollPrice.FreeRerollsResource, 1)]));
        AssertNextReroll(await MeJsonAsync(vasya), "freeRerollResource", 0);

        // Playing: no price
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("nextReroll").ValueKind);
    }

    [Fact]
    public async Task Next_reroll_price_is_a_bad_event_under_that_ruleset_and_a_paid_one_lists_a_manual_effect()
    {
        // Given the paid reroll costs a bad event
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(
            rules with { Roll = rules.Roll with { RerollCost = new RerollCost { Kind = RerollCostKind.BadEvent } } },
            ExpectedVersion: null));
        var vasya = await _site.SignedInAsync("vasya");
        var petya = await _site.SignedInAsync("petya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "reroll", new { commandId = Guid.NewGuid() });

        // Before paying: the price is a bad event, no manual effects yet
        var before = await MeJsonAsync(vasya);
        AssertNextReroll(before, "badEvent", 0);
        Assert.Equal(0, before.GetProperty("manualEffects").GetArrayLength());

        // When he pays with a bad event
        var paid = await PostAsync(vasya, "reroll", new { commandId = Guid.NewGuid() });
        Assert.Equal(["game-rerolled", "manual-effect-created", "game-rolled"], await TypesAsync(paid));

        // Then his turn lists one pending manual effect: draw a bad event, from a paid reroll
        var effect = Assert.Single((await MeJsonAsync(vasya)).GetProperty("manualEffects").EnumerateArray());
        Assert.NotEqual(Guid.Empty, effect.GetProperty("id").GetGuid());
        Assert.Equal("bad", effect.GetProperty("drawEvent").GetString());
        Assert.Equal("paidReroll", effect.GetProperty("source").GetString());
        await using (var db = _site.NewDb())
        {
            Assert.Equal(effect.GetProperty("id").GetGuid(), db.ManualEffects.Single().Id);
        }

        // And only he sees it: Петя's own turn has none
        Assert.Equal(0, (await MeJsonAsync(petya)).GetProperty("manualEffects").GetArrayLength());
    }

    [Fact]
    public async Task Manual_effects_are_empty_for_a_player_without_them()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "reroll", new { commandId = Guid.NewGuid() });

        var me = await MeJsonAsync(vasya);

        Assert.Equal(JsonValueKind.Array, me.GetProperty("manualEffects").ValueKind);
        Assert.Equal(0, me.GetProperty("manualEffects").GetArrayLength());
    }

    // ---- Drop (RR2, RR4, D-94) ----

    private static async Task<JsonElement> RunOfAsync(HttpClient client) =>
        (await MeJsonAsync(client)).GetProperty("activeRun");

    [Fact]
    public async Task Player_drops_the_game_being_played_and_pays_the_penalty()
    {
        // Given Вася is playing on the start: the penalty takes points (into the negative), there is no cell to go back to
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });
        var game = (await RunOfAsync(vasya)).GetProperty("game").GetProperty("id").GetGuid();

        var response = await PostAsync(vasya, "drop", new { commandId = Guid.NewGuid() });

        Assert.Equal(["run-dropped", "points-changed", "game-excluded", "manual-effect-created"], await TypesAsync(response));
        var me = await MeJsonAsync(vasya);
        Assert.Equal("idle", me.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("activeRun").ValueKind);
        var effect = Assert.Single(me.GetProperty("manualEffects").EnumerateArray());
        Assert.Equal("bad", effect.GetProperty("drawEvent").GetString());
        Assert.Equal("drop", effect.GetProperty("source").GetString());

        var season = await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct);
        var row = season!.Players.Single(p => p.Id == _site.Players["vasya"]);
        Assert.True(row.Points < 0, $"Points {row.Points}: a drop on the start still costs points (D-09).");
        Assert.Equal("start", row.CellId);

        await using var db = _site.NewDb();
        var exclusion = Assert.Single(db.Exclusions.ToList());
        Assert.Equal((_site.Players["vasya"], game, ExclusionReason.Dropped), (exclusion.PlayerId, exclusion.GameId, exclusion.Reason));
        Assert.Equal(Engine.Runs.RunStatus.Dropped, db.Runs.Single().Status);
        Assert.Equal(0, db.SeasonPlayers.Single(p => p.Id == _site.Players["vasya"]).Coins);
    }

    [Fact]
    public async Task Drop_after_walking_moves_the_token_back()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "complete", new { commandId = Guid.NewGuid(), difficulty = "hard" });
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });

        var response = await PostAsync(vasya, "drop", new { commandId = Guid.NewGuid() });

        Assert.Equal(
            ["run-dropped", "points-changed", "player-moved", "game-excluded", "manual-effect-created"],
            await TypesAsync(response));
    }

    [Fact]
    public async Task Drop_right_after_the_start_is_allowed_and_the_view_gives_the_hint_minutes()
    {
        // RR4: «not before an hour» is only a hint; the server gives the start time and the ruleset minutes
        var vasya = await _site.SignedInAsync("vasya");
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("dropHintMinutes").ValueKind);
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });

        var me = await MeJsonAsync(vasya);
        Assert.Equal(RulesetJson.Default().Roll.MinPlayMinutesBeforeDrop, me.GetProperty("dropHintMinutes").GetInt32());
        Assert.Equal(_site.Clock.UtcNow, me.GetProperty("activeRun").GetProperty("startedAt").GetDateTimeOffset());

        var response = await vasya.PostAsJsonAsync(Url("drop"), new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("dropHintMinutes").ValueKind);
    }

    [Fact]
    public async Task Drop_hint_minutes_are_given_until_the_minimum_is_played_by_the_server_clock()
    {
        // D-94 (5): the server decides, by its own clock and the start time, not the roll time
        var minutes = RulesetJson.Default().Roll.MinPlayMinutesBeforeDrop;
        Assert.Equal(60, minutes);
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("dropHintMinutes").ValueKind); // rolling
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(2);
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });

        // Right after the start (two hours after the roll): the minimum
        Assert.Equal(minutes, (await MeJsonAsync(vasya)).GetProperty("dropHintMinutes").GetInt32());

        // A minute before the minimum: still the minimum
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(minutes - 1);
        Assert.Equal(minutes, (await MeJsonAsync(vasya)).GetProperty("dropHintMinutes").GetInt32());

        // The minimum played: no hint
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(1);
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("dropHintMinutes").ValueKind);
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(5);
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("dropHintMinutes").ValueKind);
    }

    [Fact]
    public async Task Drop_hint_minutes_follow_the_ruleset()
    {
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(rules with { Roll = rules.Roll with { MinPlayMinutesBeforeDrop = 90 } }, ExpectedVersion: null));
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(75);

        Assert.Equal(90, (await MeJsonAsync(vasya)).GetProperty("dropHintMinutes").GetInt32());
    }

    private static void AssertDropPenalty(JsonElement me, int count, int sides, bool points, bool position, bool badEvent)
    {
        var penalty = me.GetProperty("dropPenalty");
        Assert.Equal(JsonValueKind.Object, penalty.ValueKind);
        Assert.Equal(
            (count, sides, points, position, badEvent),
            (penalty.GetProperty("count").GetInt32(), penalty.GetProperty("sides").GetInt32(), penalty.GetProperty("affectsPoints").GetBoolean(),
                penalty.GetProperty("affectsPosition").GetBoolean(), penalty.GetProperty("badEvent").GetBoolean()));
    }

    [Fact]
    public async Task Drop_penalty_is_shown_only_while_playing_by_the_default_ruleset()
    {
        // D-94 (5): the screen does not guess the penalty; the default ruleset is 2d4 on points and position and a bad event
        var drop = RulesetJson.Default().Drop;
        Assert.Equal(
            (2, 4, true, true, MandatoryEvent.Bad),
            (drop.PenaltyDice.Count, drop.PenaltyDice.Sides, drop.AffectsPoints, drop.AffectsPosition, drop.MandatoryEvent));
        var vasya = await _site.SignedInAsync("vasya");
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("dropPenalty").ValueKind);
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("dropPenalty").ValueKind);
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });

        AssertDropPenalty(await MeJsonAsync(vasya), 2, 4, points: true, position: true, badEvent: true);

        await PostAsync(vasya, "drop", new { commandId = Guid.NewGuid() });
        Assert.Equal(JsonValueKind.Null, (await MeJsonAsync(vasya)).GetProperty("dropPenalty").ValueKind);
    }

    [Fact]
    public async Task Drop_penalty_follows_the_ruleset_in_force_now_not_the_roll()
    {
        // The drop penalty is a rule of the action (D-94 (1)): a change after the start shows at once
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });
        var rules = RulesetJson.Default();

        await _site.SendAsync(new ChangeRuleset(
            rules with
            {
                Drop = rules.Drop with
                {
                    PenaltyDice = new PenaltyDice { Count = 1, Sides = 6 },
                    AffectsPosition = false,
                    MandatoryEvent = MandatoryEvent.None,
                },
            },
            ExpectedVersion: null));

        AssertDropPenalty(await MeJsonAsync(vasya), 1, 6, points: true, position: false, badEvent: false);
    }

    [Fact]
    public async Task Drop_penalty_without_points_or_position_is_still_shown()
    {
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(
            rules with { Drop = rules.Drop with { AffectsPoints = false, AffectsPosition = false } }, ExpectedVersion: null));
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });

        AssertDropPenalty(await MeJsonAsync(vasya), 2, 4, points: false, position: false, badEvent: true);
    }

    [Fact]
    public async Task Drop_before_playing_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });

        var response = await vasya.PostAsJsonAsync(Url("drop"), new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.wrongPhase", problem.RootElement.GetProperty("code").GetString());
        await using var db = _site.NewDb();
        Assert.Empty(db.Exclusions.ToList());
    }

    [Fact]
    public async Task Repeating_a_drop_with_the_same_command_id_acts_once()
    {
        // A repeated request of one click must not pay the penalty twice
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });
        var body = new { commandId = Guid.NewGuid() };

        var first = await vasya.PostAsJsonAsync(Url("drop"), body, Ct);
        var second = await vasya.PostAsJsonAsync(Url("drop"), body, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var repeat = await second.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct);
        Assert.True(repeat!.Duplicate);
        Assert.Equal((await first.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct))!.Events, repeat.Events);
        await using var db = _site.NewDb();
        Assert.Equal(1, db.Events.Count(e => e.Type == "run-dropped"));
        Assert.Equal(1, db.Events.Count(e => e.Type == "points-changed"));
    }

    [Fact]
    public async Task A_player_cannot_drop_for_another_player()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });
        var petya = await _site.SignedInAsync("petya");

        var response = await petya.PostAsJsonAsync(Url("drop"), new { commandId = Guid.NewGuid(), playerId = _site.Players["vasya"] }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.wrongPhase", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("playing", (await MeJsonAsync(vasya)).GetProperty("phase").GetString());
    }

    // ---- Tech reroll (RR5, D-94) ----

    [Fact]
    public async Task Player_tech_rerolls_within_the_window_and_gets_another_game_at_once()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });
        var game = (await RunOfAsync(vasya)).GetProperty("game").GetProperty("id").GetGuid();
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(47);

        var response = await PostAsync(vasya, "tech-reroll", new { commandId = Guid.NewGuid(), reason = "doesNotLaunch", comment = (string?)null });

        Assert.Equal(["run-tech-rerolled", "game-excluded", "game-rolled"], await TypesAsync(response));
        var me = await MeJsonAsync(vasya);
        Assert.Equal("rolling", me.GetProperty("phase").GetString());
        Assert.NotEqual(game, me.GetProperty("offer").GetProperty("id").GetGuid());
        AssertNextReroll(me, "freeThisRoll", 0); // a new roll with its own free reroll (D-07)
        await using var db = _site.NewDb();
        var exclusion = Assert.Single(db.Exclusions.ToList());
        Assert.Equal((game, ExclusionReason.TechRerolled), (exclusion.GameId, exclusion.Reason));
        Assert.Empty(db.ManualEffects.ToList());
        var player = db.SeasonPlayers.Single(p => p.Id == _site.Players["vasya"]);
        Assert.Equal((0, 0, "start"), (player.Points, player.Coins, player.CellId));
    }

    [Fact]
    public async Task Tech_reroll_with_reason_other_and_a_comment_is_logged_with_the_comment()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });

        await PostAsync(vasya, "tech-reroll", new { commandId = Guid.NewGuid(), reason = "other", comment = "Нужен руль" });

        await using var db = _site.NewDb();
        var logged = db.Events.Single(e => e.Type == "run-tech-rerolled");
        using var data = JsonDocument.Parse(logged.Data);
        Assert.Equal("Нужен руль", Property(data.RootElement, "comment").GetString());
        Assert.False(Property(data.RootElement, "byAdmin").GetBoolean());
        Assert.Equal(_site.Users["vasya"], logged.AuthorId);
    }

    [Fact]
    public async Task Tech_reroll_after_the_window_is_a_conflict()
    {
        // The window counts from the roll (D-04): 49 hours after it only the admin can tech-reroll
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(49);

        var response = await vasya.PostAsJsonAsync(Url("tech-reroll"), new { commandId = Guid.NewGuid(), reason = "weakPc" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("run.techRerollWindowClosed", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("playing", (await MeJsonAsync(vasya)).GetProperty("phase").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Tech_reroll_with_reason_other_and_no_comment_is_a_conflict(string? comment)
    {
        // The comment rule is the engine's (run.reasonCommentRequired); the API only checks the shape
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });

        var response = await vasya.PostAsJsonAsync(Url("tech-reroll"), new { commandId = Guid.NewGuid(), reason = "other", comment }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("run.reasonCommentRequired", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Tech_reroll_before_playing_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(Url("tech-reroll"), new { commandId = Guid.NewGuid(), reason = "weakPc" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.wrongPhase", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Player_cannot_mark_their_own_tech_reroll_as_the_admins()
    {
        // ByAdmin is set only by the admin endpoint: a forged field is ignored, the window still applies
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(49);

        var response = await vasya.PostAsJsonAsync(Url("tech-reroll"), new { commandId = Guid.NewGuid(), reason = "weakPc", byAdmin = true }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("run.techRerollWindowClosed", problem.RootElement.GetProperty("code").GetString());
        await using var db = _site.NewDb();
        Assert.Equal(0, db.Events.Count(e => e.Type == "run-tech-rerolled"));
    }

    [Fact]
    public async Task Forged_by_admin_inside_the_window_is_logged_as_the_players_own()
    {
        // Inside the window the player's tech reroll is accepted, but the forged field never reaches the log
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });

        await PostAsync(vasya, "tech-reroll", new { commandId = Guid.NewGuid(), reason = "weakPc", byAdmin = true });

        await using var db = _site.NewDb();
        var logged = db.Events.Single(e => e.Type == "run-tech-rerolled");
        using var data = JsonDocument.Parse(logged.Data);
        Assert.False(Property(data.RootElement, "byAdmin").GetBoolean());
        Assert.Equal(_site.Users["vasya"], logged.AuthorId);
    }

    [Fact]
    public async Task Tech_reroll_open_is_given_while_playing_until_the_window_ends()
    {
        // D-94 (5): the server says whether the window is open; it counts from the roll and is inclusive
        var vasya = await _site.SignedInAsync("vasya");
        Assert.False((await MeJsonAsync(vasya)).GetProperty("techRerollOpen").GetBoolean());
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        Assert.False((await MeJsonAsync(vasya)).GetProperty("techRerollOpen").GetBoolean()); // rolling
        var rolledAt = _site.Clock.UtcNow;
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(10);
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });

        Assert.True((await MeJsonAsync(vasya)).GetProperty("techRerollOpen").GetBoolean());

        _site.Clock.UtcNow = rolledAt.AddHours(48);
        Assert.True((await MeJsonAsync(vasya)).GetProperty("techRerollOpen").GetBoolean());

        _site.Clock.UtcNow = rolledAt.AddHours(48).AddSeconds(1);
        Assert.False((await MeJsonAsync(vasya)).GetProperty("techRerollOpen").GetBoolean());
        var refused = await vasya.PostAsJsonAsync(Url("tech-reroll"), new { commandId = Guid.NewGuid(), reason = "weakPc" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task Tech_reroll_open_follows_the_window_fixed_at_the_roll()
    {
        // D-94 (1): lengthening the window after the roll does not reopen it for that run
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(rules with { Roll = rules.Roll with { TechRerollWindowHours = 100 } }, ExpectedVersion: null));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(49);

        Assert.False((await MeJsonAsync(vasya)).GetProperty("techRerollOpen").GetBoolean());
    }

    // ---- Marks on offered games: another player dropped or tech-rerolled it (G8, D-94 (6)) ----

    /// <summary>Петя rolls and starts a game and gives it up by <paramref name="action"/>; returns the game id.</summary>
    private async Task<Guid> PetyaGivesUpAsync(string action)
    {
        var petya = await _site.SignedInAsync("petya");
        await PostAsync(petya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(petya, "start", new { commandId = Guid.NewGuid() });
        var game = (await RunOfAsync(petya)).GetProperty("game").GetProperty("id").GetGuid();
        await PostAsync(petya, action, new { commandId = Guid.NewGuid(), reason = "doesNotLaunch" });
        return game;
    }

    /// <summary>Вася rolls and gives up other offers by «Уже проходил» until <paramref name="game"/> is offered; returns the offer.</summary>
    private async Task<JsonElement> VasyaIsOfferedAsync(Guid game)
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        for (var i = 0; i < 3; i++)
        {
            var offer = (await MeJsonAsync(vasya)).GetProperty("offer");
            if (offer.GetProperty("id").GetGuid() == game)
            {
                return offer;
            }

            AssertMarks(offer); // nobody gave this one up
            await PostAsync(vasya, "already-played", new { commandId = Guid.NewGuid(), gameId = offer.GetProperty("id").GetGuid() });
        }

        throw new Xunit.Sdk.XunitException("The game given up by Петя never came to Вася.");
    }

    private static void AssertMarks(JsonElement game, params (string Player, string Kind)[] expected) =>
        Assert.Equal(
            expected,
            game.GetProperty("marks").EnumerateArray().Select(m => (m.GetProperty("playerName").GetString()!, m.GetProperty("kind").GetString()!)));

    [Fact]
    public async Task Offered_game_dropped_by_another_player_carries_the_mark()
    {
        var game = await PetyaGivesUpAsync("drop");

        var offer = await VasyaIsOfferedAsync(game);

        AssertMarks(offer, ("petya", "dropped"));
    }

    [Fact]
    public async Task Offered_game_tech_rerolled_by_another_player_carries_the_mark()
    {
        var game = await PetyaGivesUpAsync("tech-reroll");

        var offer = await VasyaIsOfferedAsync(game);

        AssertMarks(offer, ("petya", "techRerolled"));
    }

    [Fact]
    public async Task Tech_reroll_converted_to_a_drop_is_marked_as_a_drop()
    {
        var game = await PetyaGivesUpAsync("tech-reroll");
        await using (var db = _site.NewDb())
        {
            var runId = db.Runs.Single(r => r.GameId == game).Id;
            await _site.SendAsync(new Engine.Runs.ConvertTechRerollToDrop(runId, "Игра запускалась"));
        }

        var offer = await VasyaIsOfferedAsync(game);

        AssertMarks(offer, ("petya", "dropped"));
    }

    [Fact]
    public async Task Choice_options_carry_the_marks_of_their_games()
    {
        var game = await PetyaGivesUpAsync("drop");
        var vasya = await _site.SignedInAsync("vasya");
        await RollChoiceAsync(vasya);

        var options = (await MeJsonAsync(vasya)).GetProperty("choice").GetProperty("options").EnumerateArray().ToList();

        Assert.Contains(options, o => o.GetProperty("game").GetProperty("id").GetGuid() == game);
        foreach (var option in options)
        {
            var offered = option.GetProperty("game");
            if (offered.GetProperty("id").GetGuid() == game)
            {
                AssertMarks(offered, ("petya", "dropped"));
            }
            else
            {
                AssertMarks(offered);
            }
        }
    }

    [Fact]
    public async Task Offer_nobody_gave_up_has_an_empty_list_of_marks()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });

        var offer = (await MeJsonAsync(vasya)).GetProperty("offer");

        Assert.Equal(JsonValueKind.Array, offer.GetProperty("marks").ValueKind);
        AssertMarks(offer);
    }

    [Fact]
    public async Task Repeating_a_tech_reroll_with_the_same_command_id_acts_once()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });
        var body = new { commandId = Guid.NewGuid(), reason = "emulatorTooSlow" };

        var first = await vasya.PostAsJsonAsync(Url("tech-reroll"), body, Ct);
        var second = await vasya.PostAsJsonAsync(Url("tech-reroll"), body, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True((await second.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct))!.Duplicate);
        await using var db = _site.NewDb();
        Assert.Equal(1, db.Events.Count(e => e.Type == "run-tech-rerolled"));
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.EnumerateObject().Single(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private async Task<ChoiceView> RollChoiceAsync(HttpClient player)
    {
        var rules = RulesetJson.Default();
        await _site.SendAsync(new ChangeRuleset(rules with { Roll = rules.Roll with { ChoiceCount = 3 } }, ExpectedVersion: null));
        await PostAsync(player, "roll", new { commandId = Guid.NewGuid() });
        return (await player.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Choice!;
    }

    [Fact]
    public async Task Repeating_a_choice_with_the_same_command_id_acts_once()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var choice = await RollChoiceAsync(vasya);
        var body = new { commandId = Guid.NewGuid(), choiceId = choice.Id, optionId = choice.Options[0].Id };

        var first = await vasya.PostAsJsonAsync(Url("choose"), body, Ct);
        var second = await vasya.PostAsJsonAsync(Url("choose"), body, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True((await second.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct))!.Duplicate);
        await using var db = _site.NewDb();
        Assert.Equal(1, db.Events.Count(e => e.Type == "choice-made"));
    }

    // ---- Refused: no session, another role, another player ----

    [Fact]
    public async Task A_player_cannot_answer_or_see_another_players_choice()
    {
        var vasya = await _site.SignedInAsync("vasya");
        var choice = await RollChoiceAsync(vasya);

        // Petya sends Vasya's choice and option: the engine checks it against Petya's own (absent) choice
        var petya = await _site.SignedInAsync("petya");
        var response = await petya.PostAsJsonAsync(Url("choose"), new { commandId = Guid.NewGuid(), choiceId = choice.Id, optionId = choice.Options[0].Id }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.noPendingChoice", problem.RootElement.GetProperty("code").GetString());
        var mine = (await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Choice!;
        Assert.Equal(choice.Id, mine.Id);
        Assert.Equal(choice.Options.Select(o => o.Id), mine.Options.Select(o => o.Id));

        // Nobody else sees the options: Petya has no choice, the spectator and the admin have no turn at all
        Assert.Null((await petya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct))!.Me!.Choice);
        foreach (var login in new[] { "zritel", "admin" })
        {
            var other = await _site.SignedInAsync(login);
            var text = await other.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct);
            Assert.Null(JsonSerializer.Deserialize<SeasonView>(text, s_json)!.Me);
            Assert.DoesNotContain(choice.Options[0].Id, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("roll")]
    [InlineData("start")]
    [InlineData("complete")]
    [InlineData("choose")]
    [InlineData("already-played")]
    [InlineData("reroll")]
    [InlineData("drop")]
    [InlineData("tech-reroll")]
    public async Task Anonymous_is_unauthorized(string action)
    {
        var client = await _site.AnonymousAsync();

        var response = await client.PostAsJsonAsync(Url(action), new { commandId = Guid.NewGuid(), difficulty = "normal", gameId = Guid.NewGuid(), reason = "weakPc" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("zritel", "roll")]
    [InlineData("zritel", "start")]
    [InlineData("zritel", "complete")]
    [InlineData("admin", "roll")]
    [InlineData("admin", "start")]
    [InlineData("admin", "complete")]
    [InlineData("masha", "roll")]
    [InlineData("masha", "start")]
    [InlineData("masha", "complete")]
    [InlineData("zritel", "choose")]
    [InlineData("admin", "choose")]
    [InlineData("masha", "choose")]
    [InlineData("zritel", "already-played")]
    [InlineData("admin", "already-played")]
    [InlineData("masha", "already-played")]
    [InlineData("zritel", "reroll")]
    [InlineData("admin", "reroll")]
    [InlineData("masha", "reroll")]
    [InlineData("zritel", "drop")]
    [InlineData("admin", "drop")]
    [InlineData("masha", "drop")]
    [InlineData("zritel", "tech-reroll")]
    [InlineData("admin", "tech-reroll")]
    [InlineData("masha", "tech-reroll")]
    public async Task Spectator_admin_and_player_outside_the_season_are_forbidden(string login, string action)
    {
        var client = await _site.SignedInAsync(login);

        var response = await client.PostAsJsonAsync(Url(action), new { commandId = Guid.NewGuid(), difficulty = "normal", choiceId = Guid.NewGuid(), optionId = "x", gameId = Guid.NewGuid(), reason = "weakPc" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = _site.NewDb();
        Assert.Equal(4, db.Events.Count()); // season created and started, two players, nothing more
    }

    [Fact]
    public async Task A_player_cannot_act_for_another_player()
    {
        var vasya = await _site.SignedInAsync("vasya");

        // Extra fields naming another player are ignored: the session decides who acts
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid(), playerId = _site.Players["petya"] });

        var season = await vasya.GetFromJsonAsync<SeasonView>($"/api/seasons/{SiteFactory.SeasonId}", s_json, Ct);
        Assert.Equal(TurnPhase.Rolling, season!.Players.Single(p => p.Id == _site.Players["vasya"]).Phase);
        Assert.Equal(TurnPhase.Idle, season.Players.Single(p => p.Id == _site.Players["petya"]).Phase);
    }

    [Theory]
    [InlineData("roll")]
    [InlineData("start")]
    [InlineData("complete")]
    [InlineData("choose")]
    [InlineData("already-played")]
    [InlineData("reroll")]
    [InlineData("drop")]
    [InlineData("tech-reroll")]
    public async Task Post_without_the_antiforgery_token_is_refused(string action)
    {
        var vasya = await _site.SignedInAsync("vasya");
        vasya.DefaultRequestHeaders.Remove(Hosting.Csrf.HeaderName);

        var response = await vasya.PostAsJsonAsync(Url(action), new { commandId = Guid.NewGuid(), difficulty = "normal", choiceId = Guid.NewGuid(), optionId = "x", gameId = Guid.NewGuid(), reason = "weakPc" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Invalid input and rule rejections ----

    [Theory]
    [InlineData("roll", """{"commandId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("roll", """{"commandId":"not-a-guid"}""")]
    [InlineData("complete", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000001","difficulty":"impossible"}""")]
    [InlineData("complete", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000002","difficulty":"hard","estimatedHours":-3}""")]
    [InlineData("complete", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000003","difficulty":"hard","estimatedHours":100000}""")]
    [InlineData("complete", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000004"}""")]
    [InlineData("complete", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000005","difficulty":null}""")]
    [InlineData("start", "not json")]
    [InlineData("choose", """{"commandId":"5b1e2f0a-0000-0000-0000-00000000000a","choiceId":"5b1e2f0a-0000-0000-0000-0000000000ff","optionId":"a\nb"}""")]
    [InlineData("choose", """{"commandId":"5b1e2f0a-0000-0000-0000-00000000000b","choiceId":"5b1e2f0a-0000-0000-0000-0000000000ff","optionId":"<script>"}""")]
    [InlineData("choose", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000006","choiceId":"00000000-0000-0000-0000-000000000000","optionId":"a"}""")]
    [InlineData("choose", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000007","choiceId":"5b1e2f0a-0000-0000-0000-0000000000ff"}""")]
    [InlineData("choose", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000008","choiceId":"5b1e2f0a-0000-0000-0000-0000000000ff","optionId":"  "}""")]
    [InlineData("choose", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000009","choiceId":"5b1e2f0a-0000-0000-0000-0000000000ff","optionId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData("already-played", """{"commandId":"00000000-0000-0000-0000-000000000000","gameId":"5b1e2f0a-0000-0000-0000-0000000000ff"}""")]
    [InlineData("already-played", """{"commandId":"5b1e2f0a-0000-0000-0000-00000000000c","gameId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("already-played", """{"commandId":"5b1e2f0a-0000-0000-0000-00000000000d"}""")]
    [InlineData("already-played", """{"commandId":"5b1e2f0a-0000-0000-0000-00000000000e","gameId":"not-a-guid"}""")]
    [InlineData("already-played", "not json")]
    [InlineData("reroll", """{"commandId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("reroll", """{"commandId":"not-a-guid"}""")]
    [InlineData("reroll", "{}")]
    [InlineData("reroll", "not json")]
    [InlineData("drop", """{"commandId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("drop", """{"commandId":"not-a-guid"}""")]
    [InlineData("drop", "{}")]
    [InlineData("drop", "not json")]
    [InlineData("tech-reroll", """{"commandId":"00000000-0000-0000-0000-000000000000","reason":"weakPc"}""")]
    [InlineData("tech-reroll", """{"reason":"weakPc"}""")]
    [InlineData("tech-reroll", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000010"}""")]
    [InlineData("tech-reroll", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000011","reason":null}""")]
    [InlineData("tech-reroll", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000012","reason":"slowInternet"}""")]
    [InlineData("tech-reroll", """{"commandId":"5b1e2f0a-0000-0000-0000-000000000013","reason":"other","comment":42}""")]
    [InlineData("tech-reroll", "not json")]
    public async Task Invalid_input_is_a_bad_request(string action, string body)
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsync(Url(action), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tech_reroll_comment_over_500_characters_is_a_bad_request()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await PostAsync(vasya, "roll", new { commandId = Guid.NewGuid() });
        await PostAsync(vasya, "start", new { commandId = Guid.NewGuid() });

        var response = await vasya.PostAsJsonAsync(Url("tech-reroll"), new { commandId = Guid.NewGuid(), reason = "other", comment = new string('я', 501) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("playing", (await MeJsonAsync(vasya)).GetProperty("phase").GetString());
    }

    [Theory]
    [InlineData("roll")]
    [InlineData("start")]
    [InlineData("complete")]
    [InlineData("choose")]
    [InlineData("already-played")]
    [InlineData("reroll")]
    [InlineData("drop")]
    [InlineData("tech-reroll")]
    public async Task Unknown_season_is_not_found(string action)
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(
            $"/api/seasons/{Guid.NewGuid()}/{action}", new { commandId = Guid.NewGuid(), difficulty = "normal", choiceId = Guid.NewGuid(), optionId = "x", gameId = Guid.NewGuid(), reason = "weakPc" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await vasya.GetAsync($"/api/seasons/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Rule_rejection_is_a_conflict_with_the_engine_code()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(Url("start"), new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.wrongPhase", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Choosing_without_a_pending_choice_is_a_conflict()
    {
        var vasya = await _site.SignedInAsync("vasya");

        var response = await vasya.PostAsJsonAsync(Url("choose"), new { commandId = Guid.NewGuid(), choiceId = Guid.NewGuid(), optionId = "x" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("turn.noPendingChoice", problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string action, object body)
    {
        var response = await client.PostAsJsonAsync(Url(action), body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return response;
    }

    private static async Task<List<string>> TypesAsync(HttpResponseMessage response) =>
        [.. (await response.Content.ReadFromJsonAsync<CommandResponse>(s_json, Ct))!.Events.Select(e => e.Type)];
}
