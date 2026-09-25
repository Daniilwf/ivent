using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameEvent.Engine.Seasons;
using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Infrastructure.Site;
using GameEvent.Web.Tests.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GameEvent.Web.Tests.Maintenance;

/// <summary>
/// Maintenance mode (SPEC «Режим обслуживания», A8, D-121): while the flag is on the site only reads — every API write is
/// 503 with a clear code, the queue refuses every command, the scheduler waits; signing in and ending the mode stay open.
/// </summary>
public sealed class MaintenanceTests : IAsyncLifetime
{
    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(string action) => $"/api/seasons/{SiteFactory.SeasonId}/{action}";

    private MaintenanceMode Mode => _site.Services.GetRequiredService<MaintenanceMode>();

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_site_is_open_by_default_and_everyone_can_ask()
    {
        var anonymous = await _site.AnonymousAsync();

        Assert.False(await MaintenanceAsync(anonymous));
        Assert.False(Mode.IsOn);
    }

    [Fact]
    public async Task The_admin_turns_it_on_and_everyone_sees_the_banner_flag()
    {
        var admin = await _site.SignedInAsync("admin");

        var answer = await admin.PutAsJsonAsync("/api/admin/maintenance", new { on = true }, Ct);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.True(Mode.IsOn);
        Assert.True(await MaintenanceAsync(await _site.AnonymousAsync()));
    }

    [Fact]
    public async Task Under_maintenance_a_write_is_503_with_a_code_and_a_retry_while_reading_works()
    {
        var vasya = await _site.SignedInAsync("vasya");
        Mode.TurnOn();

        var roll = await vasya.PostAsJsonAsync(Url("roll"), new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, roll.StatusCode);
        Assert.Equal(MaintenanceMode.Code, await CodeAsync(roll));
        Assert.Equal(TimeSpan.FromSeconds(60), roll.Headers.RetryAfter?.Delta);
        Assert.Equal(HttpStatusCode.OK, (await vasya.GetAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await vasya.GetAsync("/api/pool", Ct)).StatusCode);
        Assert.Equal("idle", await PhaseAsync(vasya));
    }

    [Theory]
    [InlineData("PUT", "/api/auth/me/avatar")]
    [InlineData("POST", "/api/pool")]
    [InlineData("POST", "/api/bug-reports")]
    [InlineData("POST", "/api/files")]
    [InlineData("DELETE", "/api/anything")]
    public async Task Every_kind_of_write_is_refused(string method, string url)
    {
        var admin = await _site.SignedInAsync("admin");
        Mode.TurnOn();

        using var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { commandId = Guid.NewGuid() }) };
        var answer = await admin.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.StatusCode);
    }

    [Fact]
    public async Task Under_maintenance_one_still_signs_in_and_out()
    {
        Mode.TurnOn();

        var petya = await _site.SignedInAsync("petya");

        Assert.Equal(HttpStatusCode.OK, (await petya.GetAsync("/api/auth/me", Ct)).StatusCode);
        Assert.True((await petya.PostAsync("/api/auth/logout", null, Ct)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_admin_ends_it_from_the_site_and_writes_work_again()
    {
        var admin = await _site.SignedInAsync("admin");
        var vasya = await _site.SignedInAsync("vasya");
        Mode.TurnOn();

        var off = await admin.PutAsJsonAsync("/api/admin/maintenance", new { on = false }, Ct);

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False(Mode.IsOn);
        Assert.True((await vasya.PostAsJsonAsync(Url("roll"), new { commandId = Guid.NewGuid() }, Ct)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_flag_file_from_the_deploy_script_is_seen_at_once()
    {
        var vasya = await _site.SignedInAsync("vasya");

        await File.WriteAllTextAsync(Mode.FlagPath, "", Ct);
        var during = await vasya.PostAsJsonAsync(Url("roll"), new { commandId = Guid.NewGuid() }, Ct);
        File.Delete(Mode.FlagPath);
        var after = await vasya.PostAsJsonAsync(Url("roll"), new { commandId = Guid.NewGuid() }, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, during.StatusCode);
        Assert.True(after.IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_queue_refuses_every_command_under_maintenance()
    {
        // Not only the API: whatever sends a command gets a refusal, nothing is written
        var before = await EventCountAsync();
        Mode.TurnOn();

        var outcome = await _site.Services.GetRequiredService<CommandBus>()
            .SendAsync(new CommandEnvelope(Guid.NewGuid(), SiteFactory.SeasonId, new SetSeasonDeadline(_site.Clock.UtcNow.AddDays(1)), _site.Users["admin"]), Ct);

        Assert.False(outcome.IsAccepted);
        Assert.Equal(MaintenanceMode.Code, outcome.Rejection!.Code);
        Assert.Equal(before, await EventCountAsync());
    }

    [Fact]
    public async Task A_deadline_passed_under_maintenance_closes_the_season_right_after_it()
    {
        await _site.SendAsync(new SetSeasonDeadline(_site.Clock.UtcNow.AddHours(1)));
        _site.Clock.UtcNow = _site.Clock.UtcNow.AddHours(2);
        var scheduler = _site.Services.GetServices<IHostedService>().OfType<DeadlineScheduler>().Single();
        Mode.TurnOn();

        await scheduler.TickAsync(Ct);
        var during = await StatusAsync();
        Mode.TurnOff();
        await scheduler.TickAsync(Ct);

        Assert.Equal(SeasonStatus.Active, during);
        Assert.Equal(SeasonStatus.Closing, await StatusAsync());
    }

    [Theory]
    [InlineData("vasya", HttpStatusCode.Forbidden)]
    [InlineData("zritel", HttpStatusCode.Forbidden)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task Only_the_admin_turns_it_on_or_off(string? login, HttpStatusCode expected)
    {
        var client = login is null ? await _site.AnonymousAsync() : await _site.SignedInAsync(login);

        var answer = await client.PutAsJsonAsync("/api/admin/maintenance", new { on = true }, Ct);

        Assert.Equal(expected, answer.StatusCode);
        Assert.False(Mode.IsOn);
    }

    [Fact]
    public async Task A_request_without_a_body_is_invalid()
    {
        var admin = await _site.SignedInAsync("admin");

        var answer = await admin.PutAsync("/api/admin/maintenance", new StringContent("", System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.False(Mode.IsOn);
    }

    // ---- Helpers ----

    private static async Task<bool> MaintenanceAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/status", Ct));
        return doc.RootElement.GetProperty("maintenance").GetBoolean();
    }

    private static async Task<string?> PhaseAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/seasons/{SiteFactory.SeasonId}", Ct));
        return doc.RootElement.GetProperty("me").GetProperty("phase").GetString();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<int> EventCountAsync()
    {
        await using var db = _site.NewDb();
        return await db.Events.CountAsync(Ct);
    }

    private async Task<SeasonStatus> StatusAsync()
    {
        await using var db = _site.NewDb();
        return (await db.Seasons.SingleAsync(s => s.Id == SiteFactory.SeasonId, Ct)).Status;
    }
}
