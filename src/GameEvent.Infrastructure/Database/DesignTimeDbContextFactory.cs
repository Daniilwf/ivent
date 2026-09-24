using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GameEvent.Infrastructure.Database;

/// <summary>Used only by <c>dotnet ef migrations add</c>; the file is never opened.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GameEventDbContext>
{
    public GameEventDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<GameEventDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
