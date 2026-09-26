using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GameEvent.Infrastructure.Database;

/// <summary>
/// Used by <c>dotnet ef</c>: <c>migrations add</c> never opens the file; the migration check (J5) passes <c>--connection</c>
/// to <c>database update</c>, which replaces this data source.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GameEventDbContext>
{
    public GameEventDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<GameEventDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
