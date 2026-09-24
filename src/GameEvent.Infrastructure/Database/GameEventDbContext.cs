using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Database;

public sealed class GameEventDbContext(DbContextOptions<GameEventDbContext> options) : DbContext(options)
{
    public DbSet<GameEventRecord> Events => Set<GameEventRecord>();

    public DbSet<SeasonRecord> Seasons => Set<SeasonRecord>();

    public DbSet<SeasonPlayerRecord> SeasonPlayers => Set<SeasonPlayerRecord>();

    public DbSet<RunRecord> Runs => Set<RunRecord>();

    public DbSet<GameRecord> Games => Set<GameRecord>();

    public DbSet<CategoryRecord> Categories => Set<CategoryRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no native time type: full-precision UTC ticks sort and compare correctly in SQL
        // and round-trip exactly, so the projection equals the fold of the log (L4).
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameEventRecord>(e =>
        {
            e.ToTable("GameEvent");
            e.HasIndex(x => new { x.SeasonId, x.Sequence }).IsUnique();
            e.HasIndex(x => x.CommandId);
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.CommandType).HasMaxLength(100);
        });

        modelBuilder.Entity<SeasonRecord>(e =>
        {
            e.ToTable("Season");
            e.Property(x => x.Status).HasMaxLength(20);
        });

        modelBuilder.Entity<SeasonPlayerRecord>(e =>
        {
            e.ToTable("SeasonPlayer");
            e.HasOne<SeasonRecord>().WithMany().HasForeignKey(x => x.SeasonId);
            e.HasIndex(x => new { x.SeasonId, x.Points });
            e.Property(x => x.Phase).HasMaxLength(20);
        });

        modelBuilder.Entity<RunRecord>(e =>
        {
            e.ToTable("Run");
            e.HasOne<SeasonRecord>().WithMany().HasForeignKey(x => x.SeasonId);
            e.HasOne<SeasonPlayerRecord>().WithMany().HasForeignKey(x => x.PlayerId);
            e.HasIndex(x => new { x.SeasonId, x.GameId });
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.Difficulty).HasMaxLength(20);
        });

        modelBuilder.Entity<GameRecord>(e =>
        {
            e.ToTable("Game");
            e.Property(x => x.Title).HasMaxLength(300);
        });

        modelBuilder.Entity<CategoryRecord>(e =>
        {
            e.ToTable("Category");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(100);
        });
    }
}

/// <summary>Stores a moment as UTC ticks; reads it back with a zero offset.</summary>
internal sealed class UtcTicksConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
    v => v.UtcTicks,
    v => new DateTimeOffset(v, TimeSpan.Zero));

