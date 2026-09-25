using GameEvent.Engine.Accounts;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Tests.Queue;

/// <summary>The avatar in the queue (D-117): an existing, not deleted account and a stored file; the change in the global log.</summary>
public sealed class AvatarQueueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_avatar_is_set_and_logged()
    {
        await using var h = await QueueHarness.StartAsync();
        var (user, file) = await UserWithFileAsync(h);

        var outcome = await SendAsync(h, new SetAvatar(user, file));

        Assert.Equal(new AccountAvatarChanged(user, file), Assert.Single(outcome.Events).Event);
        await using var db = h.NewDb();
        Assert.Equal(file, (await db.Users.SingleAsync(u => u.Id == user, Ct)).AvatarFileId);
    }

    [Fact]
    public async Task A_file_that_is_not_stored_is_refused()
    {
        await using var h = await QueueHarness.StartAsync();
        var (user, _) = await UserWithFileAsync(h);

        Assert.Equal(AccountRules.AvatarFileUnknown, (await SendAsync(h, new SetAvatar(user, Guid.NewGuid()))).Rejection!.Code);
    }

    [Fact]
    public async Task A_deleted_or_unknown_account_is_refused()
    {
        await using var h = await QueueHarness.StartAsync();
        var (user, file) = await UserWithFileAsync(h);
        await SendAsync(h, new SeedAccount("admin", "Админ", Role.Admin, "пароль-админа-1"));
        Assert.True((await SendAsync(h, new DeleteAccount(user))).IsAccepted);

        Assert.Equal(AccountRules.Deleted, (await SendAsync(h, new SetAvatar(user, file))).Rejection!.Code);
        Assert.Equal(AccountRules.Unknown, (await SendAsync(h, new SetAvatar(Guid.NewGuid(), null))).Rejection!.Code);
    }

    [Fact]
    public async Task No_avatar_twice_changes_nothing()
    {
        await using var h = await QueueHarness.StartAsync();
        var (user, _) = await UserWithFileAsync(h);

        Assert.Equal(AccountRules.NothingToChange, (await SendAsync(h, new SetAvatar(user, null))).Rejection!.Code);
    }

    private static Task<CommandOutcome> SendAsync(QueueHarness h, ICommand command) =>
        h.Bus.SendAsync(new CommandEnvelope(Guid.NewGuid(), Guid.Empty, command, AuthorId: null), Ct);

    private static async Task<(Guid User, Guid File)> UserWithFileAsync(QueueHarness h)
    {
        var seeded = await SendAsync(h, new SeedAccount("vasya", "Вася", Role.Player, "пароль-васи-1"));
        var user = Assert.IsType<AccountCreated>(Assert.Single(seeded.Events).Event).UserId;
        var file = Guid.NewGuid();
        Assert.True((await SendAsync(h, new RecordFile(file, user, FileNames.Webp, 100, 10, 10, 1, 60))).IsAccepted);
        return (user, file);
    }
}
