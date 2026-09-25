using GameEvent.Engine.Accounts;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Queue;

/// <summary>Account commands in the queue (D-106): the global log, one at a time, secrets kept out of the log.</summary>
public sealed class AccountQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_account_command_writes_the_account_and_the_global_log_together()
    {
        await using var h = await QueueHarness.StartAsync();

        var outcome = await h.Bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, new SeedAccount("vasya", "Вася", Role.Player, "пароль-васи-1"), AuthorId: null), Ct);

        Assert.True(outcome.IsAccepted);
        var created = Assert.IsType<AccountCreated>(Assert.Single(outcome.Events).Event);
        await using var db = h.NewDb();
        var user = await db.Users.SingleAsync(Ct);
        Assert.Equal((created.UserId, "vasya", false), (user.Id, user.Login, user.MustChangePassword));
        var logged = await db.Events.SingleAsync(Ct);
        Assert.Equal((Guid.Empty, 1L, "account-created"), (logged.SeasonId, logged.Sequence, logged.Type));
        Assert.DoesNotContain("пароль-васи-1", logged.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_command_sent_to_a_season_is_refused()
    {
        await using var h = await QueueHarness.StartAsync();

        var outcome = await h.Bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.NewGuid(), new CreateAccount("vasya", "Вася", Role.Player), AuthorId: null), Ct);

        Assert.Equal(RejectionCodes.SeasonMismatch, outcome.Rejection!.Code);
        await using var db = h.NewDb();
        Assert.Empty(await db.Users.ToListAsync(Ct));
    }

    [Fact]
    public async Task The_fingerprint_of_a_password_change_leaves_the_password_out()
    {
        await using var h = await QueueHarness.StartAsync();
        var seeded = await h.Bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, new SeedAccount("vasya", "Вася", Role.Player, "пароль-васи-1"), AuthorId: null), Ct);
        var userId = Assert.IsType<AccountCreated>(Assert.Single(seeded.Events).Event).UserId;
        var commandId = Guid.NewGuid();

        var first = await h.Bus.SendAsync(new CommandEnvelope(commandId, Guid.Empty, new ChangeOwnPassword(userId, "новый-пароль-1"), userId), Ct);
        var again = await h.Bus.SendAsync(new CommandEnvelope(commandId, Guid.Empty, new ChangeOwnPassword(userId, "новый-пароль-1"), userId), Ct);

        Assert.True(first.IsAccepted);
        Assert.True(again.IsDuplicate);
        await using var db = h.NewDb();
        var logged = await db.Events.Where(e => e.CommandId == commandId).SingleAsync(Ct);
        var withoutSecret = await h.Bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, new ChangeOwnPassword(userId, "другой-пароль-2"), userId), Ct);
        await using var after = h.NewDb();
        var other = await after.Events.Where(e => e.Type == "account-password-changed" && e.CommandId != commandId).SingleAsync(Ct);
        Assert.True(withoutSecret.IsAccepted);
        Assert.Equal(logged.CommandHash, other.CommandHash);
    }
}
