using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using static GameEvent.Web.Tests.Queue.EventsTestSupport;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// SPEC «страница правил ... цифры всегда настоящие», versioned rules and undo by compensating events:
/// undo restores values as a new version, and the public rules read must describe that version for the next editor.
/// These regressions use the ordinary GET/PUT/undo APIs; no read-endpoint or undo implementation is consulted.
/// </summary>
public sealed class RulesUndoReadApiTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Rules => $"/api/seasons/{SiteFactory.SeasonId}/rules";

    private static string Admin => $"/api/admin/seasons/{SiteFactory.SeasonId}";

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Undo_reads_restored_values_as_a_new_version_with_reverse_diff_author_and_time()
    {
        var admin = await _site.SignedInAsync("admin");
        var reader = await _site.SignedInAsync("vasya");
        var original = await ReadAsync(reader);
        var oldMax = MaxDice(original);
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        var changedAt = _site.Clock.UtcNow;
        var change = Guid.NewGuid();
        await PutOkAsync(admin, original, oldMax + 2, change);
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        var undoneAt = _site.Clock.UtcNow;

        await UndoAsync(admin, change);

        var restored = await ReadAsync(reader);
        Assert.Equal(3, restored.GetProperty("version").GetInt32());
        Assert.Equal(oldMax, MaxDice(restored));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(original.GetProperty("ruleset").GetRawText()),
            JsonNode.Parse(restored.GetProperty("ruleset").GetRawText())));
        var history = restored.GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal(new[] { 3, 2, 1 }, history.Select(h => h.GetProperty("version").GetInt32()));
        Assert.Equal(undoneAt, history[0].GetProperty("at").GetDateTimeOffset());
        Assert.Equal(_site.Users["admin"], history[0].GetProperty("authorId").GetGuid());
        Assert.Equal("admin", history[0].GetProperty("authorName").GetString());
        var reverse = Assert.Single(history[0].GetProperty("changes").EnumerateArray());
        Assert.Equal("reward.diceCount.max", reverse.GetProperty("path").GetString());
        Assert.Equal((oldMax + 2).ToString(System.Globalization.CultureInfo.InvariantCulture), reverse.GetProperty("before").GetString());
        Assert.Equal(oldMax.ToString(System.Globalization.CultureInfo.InvariantCulture), reverse.GetProperty("after").GetString());
        Assert.Equal(changedAt, history[1].GetProperty("at").GetDateTimeOffset());
        Assert.Equal(_site.Users["admin"], history[1].GetProperty("authorId").GetGuid());
        Assert.Equal(JsonValueKind.Null, history[2].GetProperty("authorId").ValueKind);
        Assert.Empty(history[2].GetProperty("changes").EnumerateArray());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task A_fresh_editor_can_save_using_the_version_returned_by_get_after_an_undo()
    {
        var admin = await _site.SignedInAsync("admin");
        var original = await ReadAsync(admin);
        var change = Guid.NewGuid();
        await PutOkAsync(admin, original, MaxDice(original) + 2, change);
        await UndoAsync(admin, change);

        var fresh = await ReadAsync(admin);
        Assert.Equal(3, fresh.GetProperty("version").GetInt32());
        // The actual GET value is sent back by the editor. Never substitute an expected version to bypass a stale read.
        var result = await PutOkAsync(admin, fresh, MaxDice(original) + 3);

        Assert.Equal(4, result.GetProperty("version").GetInt32());
        var current = await ReadAsync(admin);
        Assert.Equal(4, current.GetProperty("version").GetInt32());
        Assert.Equal(MaxDice(original) + 3, MaxDice(current));
        Assert.Equal(new[] { 4, 3, 2, 1 }, current.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("version").GetInt32()));
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task An_editor_opened_before_the_undo_stays_stale_and_cannot_overwrite_the_restored_rules()
    {
        var admin = await _site.SignedInAsync("admin");
        var original = await ReadAsync(admin);
        var change = Guid.NewGuid();
        await PutOkAsync(admin, original, MaxDice(original) + 2, change);
        var stale = await ReadAsync(admin);
        Assert.Equal(2, stale.GetProperty("version").GetInt32());
        await UndoAsync(admin, change);

        var fresh = await ReadAsync(admin);
        Assert.Equal(3, fresh.GetProperty("version").GetInt32());
        var response = await PutAsync(admin, stale, MaxDice(original) + 3);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var refusal = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("ruleset.versionConflict", refusal.RootElement.GetProperty("code").GetString());
        var after = await ReadAsync(admin);
        Assert.Equal(3, after.GetProperty("version").GetInt32());
        Assert.Equal(MaxDice(original), MaxDice(after));
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task Undo_of_a_command_that_did_not_change_rules_does_not_invent_a_rules_version()
    {
        var admin = await _site.SignedInAsync("admin");
        var before = await ReadAsync(admin);
        var adjust = Guid.NewGuid();
        await ApiCalls.PostOkAsync(admin, $"{Admin}/players/{_site.Players["vasya"]}/adjust", new
        {
            commandId = adjust,
            coinsDelta = 1,
            comment = "Монетка для проверки отката",
        });

        await UndoAsync(admin, adjust);

        var after = await ReadAsync(admin);
        Assert.Equal(1, after.GetProperty("version").GetInt32());
        Assert.Single(after.GetProperty("history").EnumerateArray());
        Assert.Equal(before.GetRawText(), after.GetRawText());
        await AssertIntactAsync(_site);
    }

    [Fact]
    public async Task Same_clock_rule_changes_and_undo_are_ordered_by_version_including_the_restoration()
    {
        var admin = await _site.SignedInAsync("admin");
        var at = _site.Clock.UtcNow;
        var original = await ReadAsync(admin);
        var change = Guid.NewGuid();
        await PutOkAsync(admin, original, MaxDice(original) + 2, change);
        await UndoAsync(admin, change);
        var restored = await ReadAsync(admin);
        Assert.Equal(3, restored.GetProperty("version").GetInt32());
        await PutOkAsync(admin, restored, MaxDice(original) + 3);

        var current = await ReadAsync(admin);
        var history = current.GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal(4, current.GetProperty("version").GetInt32());
        Assert.Equal(new[] { 4, 3, 2, 1 }, history.Select(h => h.GetProperty("version").GetInt32()));
        Assert.All(history, h => Assert.Equal(at, h.GetProperty("at").GetDateTimeOffset()));
        Assert.Equal(MaxDice(original) + 3, MaxDice(current));
        await AssertIntactAsync(_site);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client) =>
        await ApiCalls.OkAsync(await client.GetAsync(Rules, Ct));

    private static int MaxDice(JsonElement rules) =>
        rules.GetProperty("ruleset").GetProperty("reward").GetProperty("diceCount").GetProperty("max").GetInt32();

    private static Task UndoAsync(HttpClient admin, Guid targetCommandId) =>
        ApiCalls.PostOkAsync(admin, $"{Admin}/undo", new { commandId = Guid.NewGuid(), targetCommandId, comment = "Отмена правки" });

    private static Task<HttpResponseMessage> PutAsync(HttpClient admin, JsonElement editorView, int maximum, Guid? commandId = null)
    {
        var ruleset = JsonNode.Parse(editorView.GetProperty("ruleset").GetRawText())!.AsObject();
        ruleset["reward"]!["diceCount"]!["max"] = maximum;
        return admin.PutAsJsonAsync($"{Admin}/rules", new
        {
            commandId = commandId ?? Guid.NewGuid(),
            expectedVersion = editorView.GetProperty("version").GetInt32(),
            ruleset,
        }, Ct);
    }

    private static async Task<JsonElement> PutOkAsync(HttpClient admin, JsonElement editorView, int maximum, Guid? commandId = null)
    {
        var response = await PutAsync(admin, editorView, maximum, commandId);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await ApiCalls.OkAsync(response);
    }
}
