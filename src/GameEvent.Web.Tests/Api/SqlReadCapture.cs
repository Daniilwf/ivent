using System.Collections.Concurrent;
using System.Data.Common;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GameEvent.Web.Tests.Api;

/// <summary>Captures the SQL reads of one test site so a projection read cannot silently start folding the log again.</summary>
internal sealed class SqlReadCapture : DbCommandInterceptor
{
    private readonly ConcurrentQueue<Read> _reads = new();

    public void AddTo(IServiceCollection services) =>
        services.AddDbContextFactory<GameEventDbContext>(options => options.AddInterceptors(this));

    public void Clear() => _reads.Clear();

    public IEnumerable<Read> From(string table) =>
        _reads.Where(read => read.Text.Contains($"FROM \"{table}\"", StringComparison.Ordinal));

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command) =>
        _reads.Enqueue(new Read(command.CommandText, [.. command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<string>()]));

    internal sealed record Read(string Text, IReadOnlyList<string> StringParameters)
    {
        public bool FiltersEventType(string type) => Text.Contains("\"Type\" =", StringComparison.Ordinal)
            && (StringParameters.Contains(type, StringComparer.Ordinal) || Text.Contains($"'{type}'", StringComparison.Ordinal));
    }
}
