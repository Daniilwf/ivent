using Microsoft.Data.Sqlite;

namespace GameEvent.Web.Hosting;

/// <summary>Where the site keeps its things when the configuration does not say: one place for the site and its commands.</summary>
public static class SitePaths
{
    public static readonly string DefaultFiles = Path.Combine("var", "files");

    public static readonly string DefaultBackups = Path.Combine("var", "backups");

    /// <summary>
    /// The site's lock (D-127): <c>Site:LockFile</c>, else <c>site.lock</c> next to the database — the same file the import
    /// tool takes — so a restore or an import refuses while the site runs even without the setting (D-214). Null for a
    /// database in memory, and with <c>Site:Lock=false</c> — only the integration tests, which run several hosts over one
    /// database in one process.
    /// </summary>
    public static string? LockFile(IConfiguration configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration["Site:LockFile"] is { Length: > 0 } file)
        {
            return Path.GetFullPath(Path.Combine(contentRoot, file));
        }

        if (string.Equals(configuration["Site:Lock"], "false", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var database = new SqliteConnectionStringBuilder(AppSetup.ResolveDataSource(configuration.ConnectionString(), contentRoot)).DataSource;
        return string.IsNullOrEmpty(database) || database == ":memory:" || database.StartsWith("file::memory:", StringComparison.Ordinal)
            ? null
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database))!, "site.lock");
    }
}
