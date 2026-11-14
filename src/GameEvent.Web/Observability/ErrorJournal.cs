using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;

namespace GameEvent.Web.Observability;

/// <summary>
/// An unhandled exception as the admin's «Ошибки» page shows it (SPEC «Наблюдаемость», A10, D-107): when, which request
/// (method and path — no query, body, headers or cookies), whose, what exception and where.
/// </summary>
public sealed record ErrorEntry(
    Guid Id, DateTimeOffset At, string Method, string Path, string? UserLogin, string ExceptionType, string Message, string StackTrace, string TraceId);

/// <summary>
/// The latest unhandled exceptions in memory (D-107): the page needs the recent ones; the full history is in the log
/// files. Lost on a restart, which the log keeps anyway.
/// </summary>
public sealed class ErrorJournal
{
    public const int Capacity = 200;
    private const int MaxStackTrace = 4000;
    private const int MaxMessage = 1000;

    private readonly ConcurrentQueue<ErrorEntry> _entries = new();

    public IReadOnlyList<ErrorEntry> Latest() => [.. _entries.Reverse()];

    public ErrorEntry Add(HttpContext http, Exception exception, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(exception);
        var entry = new ErrorEntry(
            Guid.CreateVersion7(),
            at,
            http.Request.Method,
            Printable(http.Request.Path.Value ?? ""),
            http.User.Identity?.IsAuthenticated == true && !IsVote(http.Request.Path) ? http.User.FindFirstValue(ClaimTypes.Name) : null,
            exception.GetType().FullName ?? exception.GetType().Name,
            Truncate(exception.Message, MaxMessage),
            Truncate(exception.StackTrace ?? "", MaxStackTrace),
            Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier);
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }

        return entry;
    }

    /// <summary>
    /// A vote's request names no user (D-541, D-831): the page would tell who voted in an anonymous poll. Every vote is
    /// treated so, whatever the poll: the journal does not read the season.
    /// </summary>
    internal static bool IsVote(PathString path) =>
        path.Value is { } value && value.EndsWith("/vote", StringComparison.OrdinalIgnoreCase) && value.Contains("/polls/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The path is percent-decoded: control characters are escaped so that it cannot forge a line of the log.</summary>
    internal static string Printable(string text) => text.Any(char.IsControl)
        ? string.Concat(text.Select(c => char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString()))
        : text;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>
/// Records every unhandled exception of a request in the journal and the log, then lets the standard handler answer
/// 500 with a problem that tells the client nothing about the inside (D-107).
/// </summary>
public sealed partial class JournalExceptionHandler(ErrorJournal journal, TimeProvider time, ILogger<JournalExceptionHandler> logger) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // The wall clock, not the game's: the game's clock may be what failed, and an error happened when it happened
        var entry = journal.Add(httpContext, exception, time.GetUtcNow());
        LogUnhandled(logger, exception, entry.Method, entry.Path, entry.UserLogin ?? "-", entry.Id);
        return ValueTask.FromResult(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception in {Method} {Path} for {User} (error {ErrorId})")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string path, string user, Guid errorId);
}
