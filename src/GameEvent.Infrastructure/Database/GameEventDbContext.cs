using GameEvent.Infrastructure.Accounts;
using GameEvent.Infrastructure.EventLog;
using GameEvent.Infrastructure.Pool;
using GameEvent.Infrastructure.Seasons;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Database;

public sealed class GameEventDbContext(DbContextOptions<GameEventDbContext> options) : DbContext(options)
{
    public DbSet<UserRecord> Users => Set<UserRecord>();

    public DbSet<GameEventRecord> Events => Set<GameEventRecord>();

    public DbSet<SeasonRecord> Seasons => Set<SeasonRecord>();

    public DbSet<SeasonPlayerRecord> SeasonPlayers => Set<SeasonPlayerRecord>();

    public DbSet<RulesetRecord> Rulesets => Set<RulesetRecord>();

    public DbSet<SeasonResultRecord> SeasonResults => Set<SeasonResultRecord>();

    public DbSet<PlayerGameExclusionRecord> Exclusions => Set<PlayerGameExclusionRecord>();

    public DbSet<PendingManualEffectRecord> ManualEffects => Set<PendingManualEffectRecord>();

    public DbSet<ReviewRecord> Reviews => Set<ReviewRecord>();

    public DbSet<ProofRecord> Proofs => Set<ProofRecord>();

    public DbSet<RunRecord> Runs => Set<RunRecord>();

    public DbSet<GameRecord> Games => Set<GameRecord>();

    public DbSet<CategoryRecord> Categories => Set<CategoryRecord>();

    public DbSet<Files.FileRecord> Files => Set<Files.FileRecord>();

    public DbSet<BugReports.BugReportRecord> BugReports => Set<BugReports.BugReportRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no native time type: full-precision UTC ticks sort and compare correctly in SQL
        // and round-trip exactly, so the projection equals the fold of the log (L4).
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserRecord>(e =>
        {
            e.ToTable("User");
            e.HasIndex(x => x.NormalizedLogin).IsUnique();
            e.Property(x => x.Login).HasMaxLength(64);
            e.Property(x => x.NormalizedLogin).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Role).HasMaxLength(20);
        });

        modelBuilder.Entity<Files.FileRecord>(e =>
        {
            e.ToTable("StoredFile");
            e.HasIndex(x => new { x.OwnerId, x.CreatedAt });
            e.Property(x => x.MediaType).HasMaxLength(20);
            e.Property(x => x.Kind).HasMaxLength(20);
        });

        modelBuilder.Entity<BugReports.BugReportRecord>(e =>
        {
            e.ToTable("BugReport");
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.Property(x => x.Page).HasMaxLength(500);
            e.Property(x => x.Text).HasMaxLength(4000);
            e.Property(x => x.Status).HasMaxLength(20);
        });

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
            e.Property(x => x.Name).HasMaxLength(100);
        });

        modelBuilder.Entity<RulesetRecord>(e =>
        {
            e.ToTable("Ruleset");
            e.HasKey(x => new { x.SeasonId, x.Version });
            e.HasOne<SeasonRecord>().WithMany().HasForeignKey(x => x.SeasonId);
        });

        modelBuilder.Entity<SeasonPlayerRecord>(e =>
        {
            e.ToTable("SeasonPlayer");
            e.HasOne<SeasonRecord>().WithMany().HasForeignKey(x => x.SeasonId);
            e.HasIndex(x => new { x.SeasonId, x.Points });
            e.HasIndex(x => new { x.SeasonId, x.UserId }).IsUnique();
            e.Property(x => x.Phase).HasMaxLength(20);
        });

        modelBuilder.Entity<PlayerGameExclusionRecord>(e =>
        {
            e.ToTable("PlayerGameExclusion");
            e.HasKey(x => new { x.PlayerId, x.GameId });
            e.HasOne<SeasonPlayerRecord>().WithMany().HasForeignKey(x => x.PlayerId);
            e.Property(x => x.Reason).HasMaxLength(20);
        });

        modelBuilder.Entity<PendingManualEffectRecord>(e =>
        {
            e.ToTable("PendingManualEffect");
            e.HasOne<SeasonRecord>().WithMany().HasForeignKey(x => x.SeasonId);
            e.HasOne<SeasonPlayerRecord>().WithMany().HasForeignKey(x => x.PlayerId);
            e.HasOne<RunRecord>().WithMany().HasForeignKey(x => x.RunId);
            e.HasIndex(x => new { x.SeasonId, x.PlayerId });
            e.Property(x => x.DrawEvent).HasMaxLength(20);
            e.Property(x => x.Source).HasMaxLength(30);
        });

        modelBuilder.Entity<SeasonResultRecord>(e =>
        {
            e.ToTable("SeasonResult");
            e.HasKey(x => new { x.SeasonId, x.Row });
            e.HasOne<SeasonRecord>().WithMany().HasForeignKey(x => x.SeasonId);
            e.HasOne<SeasonPlayerRecord>().WithMany().HasForeignKey(x => x.PlayerId);
        });

        modelBuilder.Entity<ProofRecord>(e =>
        {
            e.ToTable("Proof");
            e.HasKey(x => x.RunId);
            e.HasOne<RunRecord>().WithMany().HasForeignKey(x => x.RunId);
            e.HasOne<SeasonRecord>().WithMany().HasForeignKey(x => x.SeasonId);
            e.Property(x => x.Status).HasMaxLength(20);
        });

        modelBuilder.Entity<ReviewRecord>(e =>
        {
            e.ToTable("Review");
            e.HasKey(x => x.RunId);
            e.HasOne<RunRecord>().WithMany().HasForeignKey(x => x.RunId);
            e.HasOne<SeasonRecord>().WithMany().HasForeignKey(x => x.SeasonId);
            e.HasIndex(x => x.GameId);
            e.HasIndex(x => x.PlayerId);
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
            e.Property(x => x.SteamAppId).HasMaxLength(12);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.CompletionCondition).HasMaxLength(1000);
            e.Property(x => x.AuthorName).HasMaxLength(64);
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

