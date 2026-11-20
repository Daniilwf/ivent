using System.Text.Json;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Players;
using GameEvent.Engine.Rulesets;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Queue;
using Microsoft.Extensions.DependencyInjection;
using static GameEvent.Web.Tests.Api.ApiCalls;

namespace GameEvent.Web.Tests.Api;

public sealed class HallOfFameFastestFinishApiTests : IAsyncLifetime
{
    private const string RecordsUrl = "/api/hall-of-fame/records";
    private const string FirstName = "Season Vasya";
    private const string SecondName = "Season Petya";
    private readonly SiteFactory _site = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync(withSeason: false);

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Confirmed_equal_finishes_are_measured_from_activation_and_published_without_reordering_places()
    {
        var season = await CreateDraftAsync();
        using var spectator = await _site.SignedInAsync("zritel");
        using var first = await _site.SignedInAsync("vasya");
        using var second = await _site.SignedInAsync("petya");
        using var admin = await _site.SignedInAsync("admin");
        var draft = await ReadAsync(spectator, RecordsUrl);

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(3);
        await ChangeStatusAsync(admin, season.Id, "active");
        await SendAsync(season.Id, new AdjustPlayer(season.Second, "Higher score", PointsDelta: 50));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMilliseconds(12_345);
        var firstRun = await FinishRunAsync(first, season.Id, season.First);
        var secondRun = await FinishRunAsync(second, season.Id, season.Second);
        var active = await ReadAsync(spectator, RecordsUrl);

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(15);
        await ChangeStatusAsync(admin, season.Id, "closing");
        await ApproveAsync(admin, season.Id, secondRun);
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(7);
        await ApproveAsync(admin, season.Id, firstRun);
        var closing = await ReadAsync(spectator, RecordsUrl);

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(31);
        await ChangeStatusAsync(admin, season.Id, "finished");
        var finished = await ReadAsync(spectator, RecordsUrl);
        var standings = (await ReadAsync(spectator, SeasonUrl(season.Id))).GetProperty("players")
            .EnumerateArray().ToDictionary(row => row.GetProperty("id").GetGuid());
        Assert.Equal(1, standings[season.First].GetProperty("finishOrder").GetInt32());
        Assert.Equal(2, standings[season.Second].GetProperty("finishOrder").GetInt32());
        Assert.True(standings[season.First].GetProperty("points").GetInt32()
            < standings[season.Second].GetProperty("points").GetInt32());
        var history = Assert.Single((await ReadAsync(spectator, "/api/hall-of-fame")).EnumerateArray());
        Assert.Equal(season.First, Assert.Single(history.GetProperty("winners").EnumerateArray()).GetProperty("playerId").GetGuid());

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddDays(1);
        await ChangeStatusAsync(admin, season.Id, "archived");
        var archived = await ReadAsync(spectator, RecordsUrl);

        Assert.Empty(Fastest(draft));
        Assert.Empty(Fastest(active));
        Assert.Empty(Fastest(closing));
        var ties = Fastest(finished);
        Assert.Equal(2, ties.Length);
        Assert.Contains(new FinishRecord(season.Id, season.First, FirstName, 12.345m, _site.Users["vasya"]), ties);
        Assert.Contains(new FinishRecord(season.Id, season.Second, SecondName, 12.345m, _site.Users["petya"]), ties);
        Assert.Equal(ties, Fastest(archived));
    }

    [Fact]
    public async Task A_quicker_revoked_finish_does_not_hide_the_slower_confirmed_record()
    {
        var season = await CreateDraftAsync();
        using var spectator = await _site.SignedInAsync("zritel");
        using var first = await _site.SignedInAsync("vasya");
        using var second = await _site.SignedInAsync("petya");
        using var admin = await _site.SignedInAsync("admin");
        await ChangeStatusAsync(admin, season.Id, "active");

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddSeconds(5);
        var rejectedRun = await FinishRunAsync(first, season.Id, season.First);
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddSeconds(19);
        var confirmedRun = await FinishRunAsync(second, season.Id, season.Second);

        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(1);
        await ChangeStatusAsync(admin, season.Id, "closing");
        await PostOkAsync(admin, $"{AdminUrl(season.Id)}/runs/{rejectedRun}/reject",
            new { commandId = Guid.NewGuid(), comment = "Proof does not match the game" });
        await ApproveAsync(admin, season.Id, confirmedRun);
        await ChangeStatusAsync(admin, season.Id, "finished");
        await ChangeStatusAsync(admin, season.Id, "archived");

        var standings = (await ReadAsync(spectator, SeasonUrl(season.Id))).GetProperty("players").EnumerateArray();
        var revoked = Assert.Single(standings, row => row.GetProperty("id").GetGuid() == season.First);
        Assert.Equal(JsonValueKind.Null, revoked.GetProperty("finishOrder").ValueKind);
        Assert.Equal(new FinishRecord(season.Id, season.Second, SecondName, 24m, _site.Users["petya"]),
            Assert.Single(Fastest(await ReadAsync(spectator, RecordsUrl))));
    }

    [Fact]
    public async Task Equal_shortest_finishes_from_different_seasons_share_the_record()
    {
        using var spectator = await _site.SignedInAsync("zritel");
        using var player = await _site.SignedInAsync("vasya");
        using var admin = await _site.SignedInAsync("admin");

        var first = await FinishSeasonAsync(TimeSpan.FromSeconds(10), archive: true);
        var second = await FinishSeasonAsync(TimeSpan.FromSeconds(10), archive: false);
        await FinishSeasonAsync(TimeSpan.FromSeconds(12), archive: true);

        var fastest = Fastest(await ReadAsync(spectator, RecordsUrl));
        Assert.Equal(2, fastest.Length);
        Assert.Contains(first, fastest);
        Assert.Contains(second, fastest);

        await SendAsync(Guid.Empty, new DeleteAccount(_site.Users["vasya"]));
        var afterDeletion = Fastest(await ReadAsync(spectator, RecordsUrl));
        Assert.Equal(fastest.Select(finish => finish with { ProfileUserId = null }), afterDeletion);

        async Task<FinishRecord> FinishSeasonAsync(TimeSpan elapsed, bool archive)
        {
            var season = await CreateDraftAsync();
            _site.Clock.UtcNow = _site.Clock.UtcNow.AddMinutes(10);
            await ChangeStatusAsync(admin, season.Id, "active");
            _site.Clock.UtcNow += elapsed;
            var completed = await FinishRunAsync(player, season.Id, season.First);
            await ChangeStatusAsync(admin, season.Id, "closing");
            await ApproveAsync(admin, season.Id, completed);
            await ChangeStatusAsync(admin, season.Id, "finished");
            if (archive)
            {
                await ChangeStatusAsync(admin, season.Id, "archived");
            }

            return new FinishRecord(season.Id, season.First, FirstName, elapsed.Ticks / (decimal)TimeSpan.TicksPerSecond, _site.Users["vasya"]);
        }
    }

    private async Task<(Guid Id, Guid First, Guid Second)> CreateDraftAsync()
    {
        var id = await _site.CreateSeasonAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await SendAsync(id, new AddSeasonPlayer(first, _site.Users["vasya"], FirstName));
        await SendAsync(id, new AddSeasonPlayer(second, _site.Users["petya"], SecondName));
        return (id, first, second);
    }

    private async Task<Guid> FinishRunAsync(HttpClient client, Guid seasonId, Guid playerId)
    {
        var beforeFinish = $"c{RulesetJson.Default().Map.LinearLength - 1}";
        await SendAsync(seasonId, new AdjustPlayer(playerId, "Put one cell before the finish", CellId: beforeFinish));
        var url = SeasonUrl(seasonId);
        await PostOkAsync(client, $"{url}/roll");
        await PostOkAsync(client, $"{url}/start");
        await PostOkAsync(client, $"{url}/complete", new { commandId = Guid.NewGuid(), difficulty = "normal" });
        var me = (await ReadAsync(client, url)).GetProperty("me");
        Assert.NotEqual(JsonValueKind.Null, me.GetProperty("finish").ValueKind);
        return me.GetProperty("lastCompleted").GetProperty("id").GetGuid();
    }

    private async Task SendAsync(Guid seasonId, ICommand command)
    {
        var bus = _site.Services.GetRequiredService<CommandBus>();
        var result = await bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), seasonId, command, _site.Users["admin"]), Ct);
        Assert.True(result.IsAccepted, $"{command.GetType().Name}: {result.Rejection?.Detail}");
    }

    private static string SeasonUrl(Guid seasonId) => $"/api/seasons/{seasonId}";

    private static string AdminUrl(Guid seasonId) => $"/api/admin/seasons/{seasonId}";

    private static Task ChangeStatusAsync(HttpClient admin, Guid seasonId, string to) =>
        PostOkAsync(admin, $"{AdminUrl(seasonId)}/status", new { commandId = Guid.NewGuid(), to });

    private static Task ApproveAsync(HttpClient admin, Guid seasonId, Guid runId) =>
        PostOkAsync(admin, $"{AdminUrl(seasonId)}/runs/{runId}/approve", new { commandId = Guid.NewGuid(), comment = "Confirmed finish" });

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, Ct);
        return await OkAsync(response);
    }

    private static FinishRecord[] Fastest(JsonElement records)
    {
        Assert.True(records.TryGetProperty("fastestFinishes", out var finishes), "The records response must include fastestFinishes.");
        return [.. finishes.EnumerateArray().Select(row => new FinishRecord(
            row.GetProperty("seasonId").GetGuid(), row.GetProperty("playerId").GetGuid(),
            row.GetProperty("name").GetString()!, row.GetProperty("elapsedSeconds").GetDecimal(),
            row.TryGetProperty("profileUserId", out var profile) && profile.ValueKind == JsonValueKind.String ? profile.GetGuid() : null))];
    }

    private sealed record FinishRecord(Guid SeasonId, Guid PlayerId, string Name, decimal ElapsedSeconds, Guid? ProfileUserId);
}
