using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Accounts;

/// <summary>Names of accounts for views that point to them (a feed line, a rules version, a bug report), by id (D-202).</summary>
public static class UserNames
{
    /// <summary>The names of the given accounts; a deleted account keeps its name, an unknown id is missing.</summary>
    public static async Task<Dictionary<Guid, string>> NamesAsync(this GameEventDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var wanted = ids.Distinct().ToList();
        return wanted.Count == 0
            ? []
            : await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);
    }
}
