using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

public sealed partial class AdminPointsPerHourApiTests
{
    [Fact]
    public async Task Cell_points_and_a_direct_points_item_raise_the_balance_without_raising_completion_efficiency()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        await EnableItemsAsync(admin, graph: true);
        var map = JsonNode.Parse("""
            {
              "cells": [
                {"id":"start","type":"start","x":0,"y":0},
                {"id":"step","type":"empty","x":100,"y":0},
                {"id":"bonus","type":"pointsBonus","amount":7,"x":200,"y":0},
                {"id":"finish","type":"finish","x":300,"y":0}
              ],
              "edges": [
                {"from":"start","to":"step","isDefaultForward":true,"isPrimaryBackward":true},
                {"from":"step","to":"bonus","isDefaultForward":true,"isPrimaryBackward":true},
                {"from":"bonus","to":"finish","isDefaultForward":true,"isPrimaryBackward":true}
              ],
              "zones": []
            }
            """)!;
        await PostOkAsync(admin, $"{AdminSeason(SeasonId)}/map/publish",
            new { commandId = Guid.NewGuid(), map, comment = "Клетка с отдельной наградой" });
        var pointGift = JsonNode.Parse("""
            {
              "id":"point-gift","kind":"item","name":"Подарок очков",
              "description":"Добавляет десять очков отдельно от броска прохождения.",
              "rarity":"common","price":null,"window":"anytime","hostile":false,"stackable":false,
              "effect":{"target":{"selector":"self"},"actions":[{"type":"changeResource","resource":"points","amount":10}]}
            }
            """)!;
        await PublishOnlyAsync(admin, pointGift);

        var run = await CompleteAsync(player, hours: 2m, faces: [2]);
        await ApproveAsync(admin, run.Id);
        Assert.Equal(9, await BalanceAsync(player, "vasya"));
        Assert.Equal(new Metric(2, 2m, 1m), await MetricAsync(admin, "vasya"));

        var itemId = await GiveAndFindItemAsync(admin, player, "point-gift");
        await PostOkAsync(player, $"{Season(SeasonId)}/items/{itemId}/use");

        Assert.Equal(19, await BalanceAsync(player, "vasya"));
        Assert.Equal(new Metric(2, 2m, 1m), await MetricAsync(admin, "vasya"));
    }

    [Fact]
    public async Task A_later_finish_bonus_does_not_become_completion_points_in_the_hour_rate()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var first = await SignInAsync(host, "vasya");
        using var later = await SignInAsync(host, "petya");
        var beforeFinish = $"c{RulesetJson.Default().Map.LinearLength - 1}";
        foreach (var login in new[] { "vasya", "petya" })
        {
            await _site.SendAsync(new AdjustPlayer(_site.Players[login], "Перед финишем", CellId: beforeFinish));
        }

        var firstRun = await CompleteAsync(first, hours: 2m, faces: [3]);
        await ApproveAsync(admin, firstRun.Id);
        var laterRun = await CompleteAsync(later, hours: 2m, faces: [2]);
        await ApproveAsync(admin, laterRun.Id);
        Assert.True(await BalanceAsync(later, "petya") > 2, "The later finisher must really receive a separate finish bonus.");

        Assert.Equal(new Metric(2, 2m, 1m), await MetricAsync(admin, "petya"));
    }

    [Fact]
    public async Task An_after_dice_reroll_changes_actual_credited_points_without_adding_another_games_hours()
    {
        using var host = WithStatistics();
        using var admin = await SignInAsync(host, "admin");
        using var player = await SignInAsync(host, "vasya");
        await EnableItemsAsync(admin);
        var reroll = EconomyApiTests.RepositoryPack()["objects"]!.AsArray()
            .Single(item => item!["id"]!.GetValue<string>() == "reroll-dice")!.DeepClone();
        await PublishOnlyAsync(admin, reroll);
        var itemId = await GiveAndFindItemAsync(admin, player, "reroll-dice");
        var run = await CompleteAsync(player, hours: 2m, faces: [2]);
        Assert.Equal(2, await BalanceAsync(player, "vasya"));

        _random.Set(4);
        using var response = await player.PostAsJsonAsync($"{Season(SeasonId)}/items/{itemId}/use",
            new { commandId = Guid.NewGuid() }, Ct);
        var used = await OkAsync(response);
        Assert.Contains(used.GetProperty("events").EnumerateArray(),
            entry => entry.GetProperty("type").GetString() == "run-dice-rerolled");
        Assert.Equal(0, _random.Remaining);
        Assert.Equal(4, await BalanceAsync(player, "vasya"));
        await ApproveAsync(admin, run.Id);

        Assert.Equal(new Metric(4, 2m, 2m), await MetricAsync(admin, "vasya"));
    }

    private static async Task EnableItemsAsync(HttpClient admin, bool graph = false)
    {
        var current = await ReadAsync(admin, $"{Season(SeasonId)}/rules");
        var ruleset = JsonNode.Parse(current.GetProperty("ruleset").GetRawText())!;
        ruleset["features"]!["items"] = true;
        if (graph)
        {
            ruleset["features"]!["mapMode"] = "graph";
        }
        using var response = await admin.PutAsJsonAsync($"{AdminSeason(SeasonId)}/rules",
            new { commandId = Guid.NewGuid(), expectedVersion = current.GetProperty("version").GetInt32(), ruleset }, Ct);
        await OkAsync(response);
    }

    private static Task PublishOnlyAsync(HttpClient admin, JsonNode definition) =>
        PostOkAsync(admin, $"{AdminSeason(SeasonId)}/content/publish", new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = 0,
            pack = new JsonObject { ["objects"] = new JsonArray(definition) },
            comment = "Один предмет для проверки статистики",
        });

    private async Task<Guid> GiveAndFindItemAsync(HttpClient admin, HttpClient player, string objectId)
    {
        await PostOkAsync(admin, $"{AdminSeason(SeasonId)}/players/{_site.Players["vasya"]}/inventory",
            new { commandId = Guid.NewGuid(), giveObjectId = objectId, comment = "Предмет для проверки" });
        var economy = await ReadAsync(player, $"{Season(SeasonId)}/economy");
        return Assert.Single(economy.GetProperty("items").EnumerateArray())
            .GetProperty("instanceId").GetGuid();
    }

    private async Task<int> BalanceAsync(HttpClient player, string login) =>
        (await ReadAsync(player, Season(SeasonId))).GetProperty("players").EnumerateArray()
            .Single(row => row.GetProperty("id").GetGuid() == _site.Players[login]).GetProperty("points").GetInt32();
}
