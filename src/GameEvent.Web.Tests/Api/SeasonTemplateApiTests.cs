using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameEvent.Engine.Rulesets;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.EventLog;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using NetVips;

namespace GameEvent.Web.Tests.Api;

public sealed class SeasonTemplateApiTests : IAsyncLifetime
{
    private const string CreateUrl = "/api/admin/seasons";
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Guid SourceId => SiteFactory.SeasonId;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();
    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_admin_creates_an_empty_draft_with_the_sources_published_map_rules_and_live_content(bool archive)
    {
        using var host = WithTemplates();
        using var admin = await SignInAsync(host, "admin");
        using var vasya = await SignInAsync(host, "vasya");
        using var statusResponse = await admin.GetAsync("/api/status", Ct);
        Assert.True((await ApiCalls.OkAsync(statusResponse)).GetProperty("seasonTemplates").GetBoolean());
        await EventSeason.SetUpAsync(admin, EventSeason.Toast);
        await ApiCalls.PostOkAsync(admin, $"/api/test/seasons/{SourceId}/content", EventSeason.Pack(EventSeason.NotEvenSweating));
        var backgroundId = await PublishMapWithBackgroundAsync(admin);
        var runId = await EventSeason.CompleteAsync(vasya);
        await ApiCalls.PostOkAsync(admin, $"{CreateUrl}/{SourceId}/runs/{runId}/approve", new { commandId = Guid.NewGuid(), comment = "Saw the completion" });
        var sourceDeadline = _site.Clock.UtcNow.AddDays(2);
        await ApiCalls.PostOkAsync(admin, $"{CreateUrl}/{SourceId}/deadline", new { commandId = Guid.NewGuid(), deadline = sourceDeadline });
        await FinishSourceAsync(admin, archive);

        await using var db = _site.NewDb();
        var (sourceBefore, sourcePosition) = await EventLogReader.ReplaySeasonAsync(db, SourceId, Ct);
        Assert.NotEmpty(sourceBefore.Players);
        Assert.Contains(runId, sourceBefore.Runs.Keys);
        Assert.NotNull(sourceBefore.Result);
        Assert.True(sourceBefore.Catalog.Objects["toast"].Deleted);
        Assert.NotEmpty(sourceBefore.Catalog.Wheels);

        var commandId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var targetDeadline = sourceDeadline.AddDays(5);
        await ApiCalls.PostOkAsync(admin, CreateUrl, new { commandId, seasonId = targetId, name = "Draft from template", sourceSeasonId = SourceId, deadline = targetDeadline });

        var (target, _) = await EventLogReader.ReplaySeasonAsync(db, targetId, Ct);
        Assert.Equal(SeasonStatus.Draft, target.Status);
        Assert.Equal("Draft from template", target.Name);
        Assert.Equal(targetDeadline, target.Deadline);
        Assert.Equal(1, target.RulesetVersion);
        Assert.Equal(JsonSerializer.Serialize(sourceBefore.Ruleset, RulesetJson.Options), JsonSerializer.Serialize(target.Ruleset, RulesetJson.Options));
        Assert.Equal(sourceBefore.Map, target.Map);
        Assert.Equal(backgroundId, target.Map.Background?.FileId);
        Assert.Equal(sourceBefore.Catalog.Objects.Where(pair => !pair.Value.Deleted), target.Catalog.Objects);
        Assert.Equal(sourceBefore.Catalog.Wheels, target.Catalog.Wheels);
        Assert.Empty(target.Players);
        Assert.Empty(target.Runs);
        Assert.Null(target.Result);
        var targetEvents = await db.Events.Where(e => e.SeasonId == targetId).OrderBy(e => e.Sequence).ToListAsync(Ct);
        Assert.Equal(["season-created", "content-published"], targetEvents.Select(e => e.Type));
        Assert.All(targetEvents, e => Assert.Equal(commandId, e.CommandId));

        var (sourceAfter, finalPosition) = await EventLogReader.ReplaySeasonAsync(db, SourceId, Ct);
        Assert.Equal(sourcePosition, finalPosition);
        Assert.Equal(sourceBefore, sourceAfter);
    }

    [Fact]
    public async Task A_linear_season_with_no_content_can_be_copied_without_inheriting_a_deadline()
    {
        using var host = WithTemplates();
        using var admin = await SignInAsync(host, "admin");
        await ApiCalls.PostOkAsync(admin, $"{CreateUrl}/{SourceId}/deadline", new { commandId = Guid.NewGuid(), deadline = _site.Clock.UtcNow.AddDays(2) });
        using var rulesResponse = await admin.GetAsync($"/api/seasons/{SourceId}/rules", Ct);
        var rules = await ApiCalls.OkAsync(rulesResponse);
        var ruleset = JsonNode.Parse(rules.GetProperty("ruleset").GetRawText())!;
        var oldLength = ruleset["map"]!["linearLength"]!.GetValue<int>();
        ruleset["map"]!["linearLength"] = oldLength + 1;
        using var changedRules = await admin.PutAsJsonAsync($"{CreateUrl}/{SourceId}/rules", new
        {
            commandId = Guid.NewGuid(),
            expectedVersion = rules.GetProperty("version").GetInt32(),
            ruleset,
        }, Ct);
        Assert.True(changedRules.IsSuccessStatusCode, await changedRules.Content.ReadAsStringAsync(Ct));
        await FinishSourceAsync(admin);
        var targetId = Guid.NewGuid();
        await ApiCalls.PostOkAsync(admin, CreateUrl, new { commandId = Guid.NewGuid(), seasonId = targetId, name = "Linear draft", sourceSeasonId = SourceId });

        await using var db = _site.NewDb();
        var (source, _) = await EventLogReader.ReplaySeasonAsync(db, SourceId, Ct);
        var (target, _) = await EventLogReader.ReplaySeasonAsync(db, targetId, Ct);
        Assert.True(source.Deadline.HasValue);
        Assert.Null(target.Deadline);
        Assert.Equal(MapMode.Linear, target.Ruleset!.Features.MapMode);
        Assert.Equal(oldLength + 1, target.Ruleset.Map.LinearLength);
        Assert.Equal(oldLength + 1, source.Map.Cells.Count);
        Assert.Equal(source.Map, target.Map);
        Assert.Empty(target.Catalog.Objects);
        Assert.Empty(target.Catalog.Wheels);
        Assert.Empty(target.Players);
    }

    [Fact]
    public async Task A_retry_after_the_template_is_archived_has_one_effect_and_a_changed_request_conflicts()
    {
        using var host = WithTemplates();
        using var admin = await SignInAsync(host, "admin");
        await FinishSourceAsync(admin);
        var commandId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var request = new { commandId, seasonId = targetId, name = "Retried draft", sourceSeasonId = SourceId };
        using var created = await admin.PostAsJsonAsync(CreateUrl, request, Ct);
        Assert.False((await ApiCalls.OkAsync(created)).GetProperty("duplicate").GetBoolean());
        await SetStatusAsync(admin, "archived");

        using var retry = await admin.PostAsJsonAsync(CreateUrl, request, Ct);
        Assert.True((await ApiCalls.OkAsync(retry)).GetProperty("duplicate").GetBoolean());
        using var conflict = await admin.PostAsJsonAsync(CreateUrl, request with { name = "Changed title" }, Ct);
        await EventSeason.AssertConflictAsync(conflict, "command.idReused");
        await using var db = _site.NewDb();
        Assert.Equal(1, await db.Events.CountAsync(e => e.SeasonId == targetId && e.Type == "season-created", Ct));
        var (target, _) = await EventLogReader.ReplaySeasonAsync(db, targetId, Ct);
        Assert.Equal(request.name, target.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_requests_with_equivalent_names_create_only_one_draft(bool secondUsesDefaultRules)
    {
        using var host = WithTemplates();
        using var admin = await SignInAsync(host, "admin");
        await FinishSourceAsync(admin);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        var first = admin.PostAsJsonAsync(CreateUrl, new { commandId = Guid.NewGuid(), seasonId = firstId, name = "New draft", sourceSeasonId = SourceId }, Ct);
        var second = admin.PostAsJsonAsync(CreateUrl, new
        {
            commandId = Guid.NewGuid(),
            seasonId = secondId,
            name = " new DRAFT ",
            sourceSeasonId = secondUsesDefaultRules ? (Guid?)null : SourceId,
        }, Ct);
        await Task.WhenAll(first, second);
        using var firstResponse = await first;
        using var secondResponse = await second;

        Assert.True(firstResponse.IsSuccessStatusCode || secondResponse.IsSuccessStatusCode);
        Assert.False(firstResponse.IsSuccessStatusCode && secondResponse.IsSuccessStatusCode);
        await EventSeason.AssertConflictAsync(firstResponse.IsSuccessStatusCode ? secondResponse : firstResponse, "season.nameTaken");
        await AssertNoSeasonAsync(firstResponse.IsSuccessStatusCode ? secondId : firstId);
    }

    [Fact]
    public async Task An_incompatible_archived_pack_refuses_the_whole_clone_and_preserves_the_source()
    {
        using var host = WithTemplates();
        using var admin = await SignInAsync(host, "admin");
        await EventSeason.SetUpAsync(admin, EventSeason.Toast);
        await EventSeason.SwitchEventsAsync(admin, on: false);
        await FinishSourceAsync(admin, archive: true);
        await using var db = _site.NewDb();
        var (sourceBefore, sourcePosition) = await EventLogReader.ReplaySeasonAsync(db, SourceId, Ct);
        Assert.NotNull(sourceBefore.Catalog.Live("toast"));
        Assert.False(sourceBefore.Ruleset!.Features.Events);
        var targetId = Guid.NewGuid();

        using var response = await admin.PostAsJsonAsync(CreateUrl, new { commandId = Guid.NewGuid(), seasonId = targetId, name = "Draft with incompatible template", sourceSeasonId = SourceId }, Ct);

        await EventSeason.AssertConflictAsync(response, "content.invalid");
        await AssertNoSeasonAsync(targetId);
        var (sourceAfter, finalPosition) = await EventLogReader.ReplaySeasonAsync(db, SourceId, Ct);
        Assert.Equal(sourcePosition, finalPosition);
        Assert.Equal(sourceBefore, sourceAfter);
    }

    [Fact]
    public async Task An_unknown_source_returns_not_found_without_creating_the_draft()
    {
        using var host = WithTemplates();
        using var admin = await SignInAsync(host, "admin");
        var targetId = Guid.NewGuid();

        using var response = await admin.PostAsJsonAsync(CreateUrl, new { commandId = Guid.NewGuid(), seasonId = targetId, name = "Missing template draft", sourceSeasonId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoSeasonAsync(targetId);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("active")]
    [InlineData("closing")]
    public async Task An_unfinished_source_is_refused_without_creating_the_draft(string status)
    {
        using var host = WithTemplates();
        using var admin = await SignInAsync(host, "admin");
        var sourceId = SourceId;
        if (status == "draft")
        {
            sourceId = Guid.NewGuid();
            await ApiCalls.PostOkAsync(admin, CreateUrl, new { commandId = Guid.NewGuid(), seasonId = sourceId, name = "Existing draft" });
        }
        else if (status == "closing")
        {
            await SetStatusAsync(admin, "closing");
        }
        var targetId = Guid.NewGuid();

        using var response = await admin.PostAsJsonAsync(CreateUrl, new { commandId = Guid.NewGuid(), seasonId = targetId, name = "An unfinished template draft", sourceSeasonId = sourceId }, Ct);

        await EventSeason.AssertConflictAsync(response, "season.templateUnavailable");
        await AssertNoSeasonAsync(targetId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_empty_source_id_or_using_the_draft_as_its_own_source_is_invalid(bool sameAsTarget)
    {
        using var host = WithTemplates();
        using var admin = await SignInAsync(host, "admin");
        var targetId = Guid.NewGuid();

        using var response = await admin.PostAsJsonAsync(CreateUrl, new
        {
            commandId = Guid.NewGuid(),
            seasonId = targetId,
            name = "Invalid template draft",
            sourceSeasonId = sameAsTarget ? targetId : Guid.Empty,
        }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoSeasonAsync(targetId);
    }

    [Theory]
    [InlineData("vasya")]
    [InlineData("zritel")]
    public async Task A_non_admin_cannot_create_from_a_template(string login)
    {
        using var host = WithTemplates();
        using var admin = await SignInAsync(host, "admin");
        using var rejected = await SignInAsync(host, login);
        await FinishSourceAsync(admin);
        var targetId = Guid.NewGuid();

        using var response = await rejected.PostAsJsonAsync(CreateUrl, new { commandId = Guid.NewGuid(), seasonId = targetId, name = "Forbidden template draft", sourceSeasonId = SourceId }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoSeasonAsync(targetId);
    }

    [Fact]
    public async Task Templates_are_opt_in_and_the_default_create_path_remains_available()
    {
        using var admin = await _site.SignedInAsync("admin");
        await FinishSourceAsync(admin);
        var targetId = Guid.NewGuid();
        using var response = await admin.PostAsJsonAsync(CreateUrl, new { commandId = Guid.NewGuid(), seasonId = targetId, name = "Disabled template draft", sourceSeasonId = SourceId }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNoSeasonAsync(targetId);

        await ApiCalls.PostOkAsync(admin, CreateUrl, new { commandId = Guid.NewGuid(), seasonId = targetId, name = "Ordinary draft" });
        await using var db = _site.NewDb();
        Assert.True(await db.Seasons.AnyAsync(s => s.Id == targetId && s.Status == SeasonStatus.Draft, Ct));
    }

    private WebApplicationFactory<Program> WithTemplates() =>
        _site.WithWebHostBuilder(builder => builder.UseSetting("Site:SeasonTemplates", "true"));

    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> host, string login)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        await SiteFactory.RefreshCsrfAsync(client);
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { login, password = SiteFactory.Password }, Ct);
        response.EnsureSuccessStatusCode();
        await SiteFactory.RefreshCsrfAsync(client);
        return client;
    }

    private static Task SetStatusAsync(HttpClient admin, string to) =>
        ApiCalls.PostOkAsync(admin, $"{CreateUrl}/{SourceId}/status", new { commandId = Guid.NewGuid(), to });

    private static async Task FinishSourceAsync(HttpClient admin, bool archive = false)
    {
        await SetStatusAsync(admin, "closing");
        await SetStatusAsync(admin, "finished");
        if (archive)
            await SetStatusAsync(admin, "archived");
    }

    private static async Task<Guid> PublishMapWithBackgroundAsync(HttpClient admin)
    {
        const string GraphJson = """
            {
              "cells": [
                { "id": "start", "type": "start", "x": 30, "y": 100 },
                { "id": "middle", "type": "empty", "x": 140, "y": 100 },
                { "id": "finish", "type": "finish", "x": 270, "y": 100 }
              ],
              "edges": [
                { "from": "start", "to": "middle", "isDefaultForward": true, "isPrimaryBackward": true },
                { "from": "middle", "to": "finish", "isDefaultForward": true, "isPrimaryBackward": true }
              ],
              "zones": []
            }
            """;
        using var picture = Image.Black(300, 200, bands: 3);
        using var form = new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "commandId" } };
        using var file = new ByteArrayContent(picture.PngsaveBuffer());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "map.png");
        var mapUrl = $"{CreateUrl}/{SourceId}/map";
        using var upload = await admin.PostAsync($"{mapUrl}/background", form, Ct);
        var background = await ApiCalls.OkAsync(upload);
        var id = background.GetProperty("id").GetGuid();
        var map = JsonNode.Parse(GraphJson)!;
        map["background"] = new JsonObject
        {
            ["fileId"] = id,
            ["width"] = background.GetProperty("width").GetInt32(),
            ["height"] = background.GetProperty("height").GetInt32(),
        };
        await ApiCalls.PostOkAsync(admin, $"{mapUrl}/publish", new { commandId = Guid.NewGuid(), map, comment = "Publish map with background" });
        return id;
    }

    private async Task AssertNoSeasonAsync(Guid seasonId)
    {
        await using var db = _site.NewDb();
        Assert.False(await db.Seasons.AnyAsync(s => s.Id == seasonId, Ct));
        Assert.False(await db.Events.AnyAsync(e => e.SeasonId == seasonId, Ct));
    }
}
