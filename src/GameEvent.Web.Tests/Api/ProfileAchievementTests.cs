using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Content;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using static GameEvent.Web.Tests.Api.ApiCalls;
using static GameEvent.Web.Tests.Api.PollSeason;

namespace GameEvent.Web.Tests.Api;

/// <summary>The seasonal achievement from CONTENT.md reaches the signed-in player's public profile through the queue.</summary>
public sealed class ProfileAchievementTests : IAsyncLifetime
{
    private const string Attacker = "vasya";
    private const string Recipient = "petya";
    private const string AttackItem = "bird-thief";
    private const string AchievementId = "unbreakable";
    private const string Title = "Непробиваемый";
    private const string Description = "Переживи пять атак за сезон.";
    private const string Definition = """
        {
          "id": "unbreakable", "kind": "achievement", "name": "Непробиваемый",
          "description": "Переживи пять атак за сезон.", "scope": "season",
          "effect": { "trigger": "hostileIncoming", "condition": { "stat": "hostileReceived", "gte": 5 },
            "actions": [{ "type": "changeResource", "resource": "coins", "amount": 5 }] }
        }
        """;

    private readonly SiteFactory _site = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();
    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task A_spectator_and_the_recipient_see_one_badge_until_the_awarding_attack_is_undone()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var attacker = await _site.SignedInAsync(Attacker);
        using var recipient = await _site.SignedInAsync(Recipient);
        using var spectator = await _site.SignedInAsync("zritel");
        await PrepareAttackAsync();

        for (var previous = 0; previous < 4; previous++)
        {
            await GiveAttackAsync(admin);
            await AttackAsync(attacker);
        }

        await GiveAttackAsync(admin);
        await GiveAttackAsync(admin);
        var earnedAt = _site.Clock.UtcNow;
        var fifthAttack = await AttackAsync(attacker);

        AssertBadge(Assert.Single(await AchievementsAsync(spectator, Recipient)), earnedAt);
        AssertBadge(Assert.Single(await AchievementsAsync(recipient, Recipient)), earnedAt);
        Assert.Empty(await AchievementsAsync(spectator, Attacker));

        using var anonymous = await _site.AnonymousAsync();
        using var denied = await anonymous.GetAsync(Profile(Recipient), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        _site.Clock.Advance(TimeSpan.FromMinutes(1));
        var sixthAttack = await AttackAsync(attacker);
        AssertBadge(Assert.Single(await AchievementsAsync(spectator, Recipient)), earnedAt);

        await UndoAsync(admin, sixthAttack);
        AssertBadge(Assert.Single(await AchievementsAsync(spectator, Recipient)), earnedAt);

        await UndoAsync(admin, fifthAttack);
        Assert.Empty(await AchievementsAsync(spectator, Recipient));
        Assert.Empty(await AchievementsAsync(recipient, Recipient));
    }

    [Fact]
    public async Task A_badge_remains_visible_in_the_archived_season()
    {
        using var admin = await _site.SignedInAsync("admin");
        using var attacker = await _site.SignedInAsync(Attacker);
        using var spectator = await _site.SignedInAsync("zritel");
        await PrepareAttackAsync();

        for (var hit = 0; hit < 5; hit++)
        {
            await GiveAttackAsync(admin);
            await AttackAsync(attacker);
        }

        foreach (var status in new[] { "closing", "finished", "archived" })
        {
            await PostOkAsync(admin, AdminUrl("status"), new { commandId = Guid.NewGuid(), to = status });
        }

        var profile = await OkAsync(await spectator.GetAsync(Profile(Recipient), Ct));
        Assert.Equal("archived", Assert.Single(profile.GetProperty("seasons").EnumerateArray()).GetProperty("status").GetString());
        AssertBadge(Assert.Single(AchievementsOf(profile)), _site.Clock.UtcNow);
    }

    [Fact]
    public async Task A_profile_from_a_season_without_achievements_returns_an_empty_array()
    {
        using var spectator = await _site.SignedInAsync("zritel");
        var profile = await OkAsync(await spectator.GetAsync(Profile(Recipient), Ct));

        Assert.NotEmpty(profile.GetProperty("seasons").EnumerateArray());
        Assert.Empty(AchievementsOf(profile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_admin_can_publish_the_documented_achievement_when_its_flag_is_enabled(bool itemsEnabled)
    {
        using var admin = await _site.SignedInAsync("admin");
        await EnableAchievementsAsync(itemsEnabled);
        var view = await OkAsync(await admin.GetAsync(AdminUrl("content"), Ct));
        Assert.Equal(itemsEnabled, view.GetProperty("itemsEnabled").GetBoolean());
        Assert.True(view.GetProperty("achievementsEnabled").GetBoolean());
        var objects = itemsEnabled
            ? new JsonArray(BirdDefinition(), JsonNode.Parse(Definition))
            : new JsonArray(JsonNode.Parse(Definition));
        var pack = new { objects, wheels = Array.Empty<object>() };
        await PostOkAsync(admin, AdminUrl("content/publish"), new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = 0,
            pack,
            comment = "Настройка сезонного достижения",
        });
    }

    [Fact]
    public async Task Achievements_only_preflight_refuses_a_pack_that_cannot_be_published()
    {
        using var admin = await _site.SignedInAsync("admin");
        await EnableAchievementsAsync(itemsEnabled: false);
        var pack = new { objects = new JsonArray(BirdDefinition(), JsonNode.Parse(Definition)), wheels = Array.Empty<object>() };

        using var response = await admin.PostAsJsonAsync(AdminUrl("content/check"), pack, Ct);
        var preflight = await OkAsync(response);
        Assert.False(preflight.GetProperty("valid").GetBoolean());
        Assert.Contains(preflight.GetProperty("problems").EnumerateArray(), problem => problem.GetProperty("path").GetString() == "objects");
    }

    [Fact]
    public async Task The_documented_horror_marathon_reaches_the_profile_on_the_third_completion_and_can_be_undone()
    {
        const string Horror = """
            {
              "id": "horror-marathon", "kind": "achievement", "name": "Хоррор-марафон",
              "description": "Пройди три хоррора подряд.", "scope": "season",
              "effect": { "trigger": "runCompleted", "condition": { "stat": "completedStreakWithTag", "tag": "Horror", "gte": 3 },
                "actions": [{ "type": "changeResource", "resource": "coins", "amount": 5 }] }
            }
            """;
        using var admin = await _site.SignedInAsync("admin");
        using var player = await _site.SignedInAsync(Recipient);
        using var spectator = await _site.SignedInAsync("zritel");
        await EnableAchievementsAsync(itemsEnabled: false);
        var pack = new { objects = new JsonArray(JsonNode.Parse(Horror)), wheels = Array.Empty<object>() };
        using var checkedPack = await admin.PostAsJsonAsync(AdminUrl("content/check"), pack, Ct);
        Assert.True((await OkAsync(checkedPack)).GetProperty("valid").GetBoolean());
        await PostOkAsync(admin, AdminUrl("content/publish"), new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = 0,
            pack,
            comment = "Награда за серию прохождений",
        });

        Guid lastCompletion = default;
        for (var done = 1; done <= 3; done++)
        {
            _site.Clock.Advance(TimeSpan.FromMinutes(10));
            await PostOkAsync(player, Url("roll"));
            await PostOkAsync(player, Url("start"));
            lastCompletion = Guid.NewGuid();
            await PostOkAsync(player, Url("complete"), new { commandId = lastCompletion, difficulty = "normal" });
            var awards = (await AchievementsAsync(spectator, Recipient)).ToArray();
            if (done < 3)
            {
                Assert.Empty(awards);
                var state = await OkAsync(await player.GetAsync(Url(""), Ct));
                var completed = state.GetProperty("me").GetProperty("lastCompleted").GetProperty("id").GetGuid();
                await PostOkAsync(admin, AdminUrl($"runs/{completed}/approve"), new
                {
                    commandId = Guid.NewGuid(),
                    comment = "Прохождение проверено",
                });
            }
            else
            {
                var award = Assert.Single(awards);
                Assert.Equal("horror-marathon", award.GetProperty("objectId").GetString());
                Assert.Equal("Хоррор-марафон", award.GetProperty("name").GetString());
                Assert.Equal(_site.Clock.UtcNow, award.GetProperty("earnedAt").GetDateTimeOffset());
            }
        }

        await PostOkAsync(admin, AdminUrl("undo"), new
        {
            commandId = Guid.NewGuid(),
            targetCommandId = lastCompletion,
            comment = "Ошибочная отметка о прохождении",
        });
        Assert.Empty(await AchievementsAsync(spectator, Recipient));
    }

    private async Task PrepareAttackAsync()
    {
        await EnableAchievementsAsync(itemsEnabled: true);
        var pack = new ContentPack
        {
            Objects = [ContentJson.Parse<ObjectDefinition>(BirdDefinition().ToJsonString()), ContentJson.Parse<ObjectDefinition>(Definition)],
        };
        await _site.SendAsync(new PublishContent(pack, "Настройка сезонного достижения"));
        await _site.SendAsync(new AdjustPlayer(_site.Players[Recipient], "Монетки для проверки атаки", CoinsDelta: 30));
    }

    private Task EnableAchievementsAsync(bool itemsEnabled)
    {
        var rules = RulesetJson.Default();
        return _site.SendAsync(new ChangeRuleset(rules with
        {
            Features = rules.Features with { Items = itemsEnabled, Achievements = true },
        }));
    }

    private static JsonNode BirdDefinition() => EconomyApiTests.RepositoryPack()["objects"]!.AsArray()
        .OfType<JsonObject>().Single(item => item["id"]!.GetValue<string>() == AttackItem).DeepClone();

    private Task GiveAttackAsync(HttpClient admin) => PostOkAsync(admin, AdminUrl($"players/{_site.Players[Attacker]}/inventory"),
        new { commandId = Guid.NewGuid(), giveObjectId = AttackItem, comment = "Предмет для атаки" });

    private async Task<Guid> AttackAsync(HttpClient attacker)
    {
        var economy = await OkAsync(await attacker.GetAsync(Url("economy"), Ct));
        var bird = economy.GetProperty("items").EnumerateArray()
            .First(item => item.GetProperty("objectId").GetString() == AttackItem);
        var commandId = Guid.NewGuid();
        await PostOkAsync(attacker, Url($"items/{bird.GetProperty("instanceId").GetGuid()}/use"),
            new { commandId, targetPlayerId = _site.Players[Recipient] });
        return commandId;
    }

    private static Task UndoAsync(HttpClient admin, Guid attack) => PostOkAsync(admin, AdminUrl("undo"),
        new { commandId = Guid.NewGuid(), targetCommandId = attack, comment = "Атака ошибочной цели" });

    private string Profile(string login) => $"/api/users/{_site.Users[login]}";

    private async Task<JsonElement.ArrayEnumerator> AchievementsAsync(HttpClient reader, string login) =>
        AchievementsOf(await OkAsync(await reader.GetAsync(Profile(login), Ct)));

    private static JsonElement.ArrayEnumerator AchievementsOf(JsonElement profile)
    {
        Assert.True(profile.TryGetProperty("achievements", out var badges), "The profile must include an achievements array.");
        Assert.Equal(JsonValueKind.Array, badges.ValueKind);
        return badges.EnumerateArray();
    }

    private static void AssertBadge(JsonElement award, DateTimeOffset earnedAt) =>
        Assert.Equal((SiteFactory.SeasonId, AchievementId, Title, Description, earnedAt),
            (award.GetProperty("seasonId").GetGuid(), award.GetProperty("objectId").GetString(),
                award.GetProperty("name").GetString(), award.GetProperty("description").GetString(),
                award.GetProperty("earnedAt").GetDateTimeOffset()));
}
