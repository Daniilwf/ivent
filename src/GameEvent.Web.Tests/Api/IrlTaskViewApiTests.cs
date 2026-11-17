using System.Text.Json;
using static GameEvent.Web.Tests.Api.EventSeason;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Task 5.21c: an IRL task is readable without the player guessing its object id. Its name and instructions come from
/// the content's last published definition; the upload limit in the turn is the season's current configured value.
/// Proof authorization and privacy remain covered by IrlProofApiTests.
/// </summary>
public sealed class IrlTaskViewApiTests : IAsyncLifetime
{
    private const string Cooking = """
        { "id": "cooking", "kind": "event", "name": "По рецепту",
          "description": "Приготовь блюдо и приложи фото.",
          "rarity": "common", "price": null, "hostile": false, "manual": true, "proof": "media" }
        """;

    private const string ChangedKaraoke = """
        { "id": "karaoke", "kind": "event", "name": "Караоке втроём",
          "description": "Спой вместе с двумя друзьями и приложи видео.",
          "rarity": "common", "price": null, "hostile": false, "manual": true, "proof": "media" }
        """;

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Two_waiting_irl_tasks_each_have_their_own_name_and_instructions()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Karaoke, 5, Cooking);
        await DropAsync(vasya);
        await PublishAsync(admin, [Karaoke, Cooking], "cooking");
        await DropAsync(vasya);

        var effects = (await MeAsync(vasya)).GetProperty("manualEffects").EnumerateArray().ToArray();

        Assert.Equal(2, effects.Length);
        var karaoke = Assert.Single(effects, effect => effect.GetProperty("objectId").GetString() == "karaoke");
        var cooking = Assert.Single(effects, effect => effect.GetProperty("objectId").GetString() == "cooking");
        Assert.True(karaoke.GetProperty("irl").GetBoolean());
        Assert.True(cooking.GetProperty("irl").GetBoolean());
        Assert.Equal("Караоке", karaoke.GetProperty("name").GetString());
        Assert.Equal("Спой песню на выбор остальных и скинь видео в галерею.", karaoke.GetProperty("description").GetString());
        Assert.Equal("По рецепту", cooking.GetProperty("name").GetString());
        Assert.Equal("Приготовь блюдо и приложи фото.", cooking.GetProperty("description").GetString());
        Assert.NotEqual(karaoke.GetProperty("id").GetGuid(), cooking.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_latest_published_words_remain_readable_after_the_task_definition_is_removed()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Karaoke);
        await DropAsync(vasya);
        var first = await SingleEffectAsync(vasya);
        Assert.Equal("Караоке", first.GetProperty("name").GetString());

        await PublishAsync(admin, [ChangedKaraoke], "karaoke");

        var revised = await SingleEffectAsync(vasya);
        Assert.Equal(first.GetProperty("id").GetGuid(), revised.GetProperty("id").GetGuid());
        Assert.Equal("Караоке втроём", revised.GetProperty("name").GetString());
        Assert.Equal("Спой вместе с двумя друзьями и приложи видео.", revised.GetProperty("description").GetString());

        await PublishAsync(admin, [], null);

        var removed = await SingleEffectAsync(vasya);
        Assert.Equal(first.GetProperty("id").GetGuid(), removed.GetProperty("id").GetGuid());
        Assert.Equal("karaoke", removed.GetProperty("objectId").GetString());
        Assert.Equal("Караоке втроём", removed.GetProperty("name").GetString());
        Assert.Equal("Спой вместе с двумя друзьями и приложи видео.", removed.GetProperty("description").GetString());
        Assert.True(removed.GetProperty("irl").GetBoolean());
    }

    [Fact]
    public async Task A_task_removed_before_its_first_view_still_has_the_last_definition()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Karaoke);
        await DropAsync(vasya);
        await PublishAsync(admin, [], null);

        var effect = await SingleEffectAsync(vasya);

        Assert.Equal("Караоке", effect.GetProperty("name").GetString());
        Assert.Equal("Спой песню на выбор остальных и скинь видео в галерею.", effect.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task The_turn_reports_the_configured_picture_limit_including_link_only_zero(int maxFiles)
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Karaoke, maxFiles);

        var turn = await MeAsync(vasya);

        Assert.Equal(maxFiles, turn.GetProperty("irlProofMaxFiles").GetInt32());
        Assert.True(turn.GetProperty("eventsEnabled").GetBoolean());
    }

    [Fact]
    public async Task Switching_events_off_does_not_turn_the_configured_picture_limit_into_zero()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        await SetUpAsync(admin, Karaoke, maxFiles: 5);
        await SwitchEventsAsync(admin, on: false, maxFiles: 5);

        var turn = await MeAsync(vasya);

        Assert.False(turn.GetProperty("eventsEnabled").GetBoolean());
        Assert.Equal(5, turn.GetProperty("irlProofMaxFiles").GetInt32());
    }

    [Fact]
    public async Task A_ruleset_without_the_irl_section_reports_zero_instead_of_an_invented_limit()
    {
        await RulesAsync(_site, rules => rules with { Events = null });

        var turn = await MeAsync(await _site.SignedInAsync("vasya"));

        Assert.Equal(0, turn.GetProperty("irlProofMaxFiles").GetInt32());
        Assert.False(turn.GetProperty("eventsEnabled").GetBoolean());
    }

    [Fact]
    public async Task An_old_manual_effect_without_an_object_keeps_null_name_and_description()
    {
        var vasya = await _site.SignedInAsync("vasya");
        await DropAsync(vasya);

        var effect = await SingleEffectAsync(vasya);

        Assert.Equal(JsonValueKind.Null, effect.GetProperty("objectId").ValueKind);
        Assert.False(effect.GetProperty("irl").GetBoolean());
        Assert.Equal(JsonValueKind.Null, effect.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, effect.GetProperty("description").ValueKind);
    }

    private static async Task PublishAsync(HttpClient admin, string[] definitions, string? badEvent)
    {
        var editor = await ApiCalls.OkAsync(await admin.GetAsync($"{Admin}/content", Ct));
        var objects = definitions.Select(definition => JsonSerializer.Deserialize<JsonElement>(definition)).ToArray();
        var wheels = badEvent is null
            ? Array.Empty<JsonElement>()
            : new[] { JsonSerializer.Deserialize<JsonElement>($$"""
                { "id": "bad", "name": "Плохие ивенты", "deck": "bad",
                  "entries": [{ "objectId": "{{badEvent}}", "weight": 1 }] }
                """) };
        await ApiCalls.PostOkAsync(admin, $"{Admin}/content/publish", new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = editor.GetProperty("version").GetInt32(),
            pack = new { objects, wheels },
            comment = "Обновлён пакет ивентов",
        });
    }
}
