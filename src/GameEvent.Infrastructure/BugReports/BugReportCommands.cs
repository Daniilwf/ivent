using GameEvent.Engine.BugReports;
using GameEvent.Engine.Kernel;

namespace GameEvent.Infrastructure.BugReports;

/// <summary>A signed-in user reports a bug (D-121); the queue gives the report its id.</summary>
public sealed record ReportBug(Guid AuthorId, string Page, string Text, BugReportContext Context, Guid? ScreenshotFileId) : Queue.IGlobalCommand;

/// <summary>The admin moves a report to another status.</summary>
public sealed record SetBugReportStatus(Guid ReportId, BugReportStatus Status) : Queue.IGlobalCommand;

/// <summary>A bug report as the admin reads it (SPEC data model «BugReport»); the context is kept as it came, in JSON.</summary>
public sealed class BugReportRecord
{
    public Guid Id { get; set; }

    public Guid AuthorId { get; set; }

    public required string Page { get; set; }

    public required string Text { get; set; }

    /// <summary>The <see cref="BugReportContext"/> in JSON: shown and exported, never filtered on (invariant 9).</summary>
    public required string ContextJson { get; set; }

    public Guid? ScreenshotFileId { get; set; }

    public BugReportStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>What a report may hold (D-121): enough to reproduce a bug, not a place to store files or logs.</summary>
public static class BugReportRules
{
    public const int MaxPageLength = 500;
    public const int MaxTextLength = 4000;

    // As much as the page gathers (web/src/app/bugContext.ts), no more: the log keeps every report for good
    public const int MaxActions = 30;
    public const int MaxErrors = 20;
    public const int MaxEntryLength = 300;
    public const int MaxUserAgentLength = 500;
    public const int MaxViewportLength = 50;

    /// <summary>Screenshots a user stores in 24 hours, apart from the upload limit: a user at that limit still reports.</summary>
    public const int ScreenshotsPerDay = 20;

    /// <summary>A screenshot is a still picture of a page: a few megabytes at most.</summary>
    public const long ScreenshotMaxBytes = 3 * 1024 * 1024;

    public const string Invalid = "bugReport.invalid";
    public const string AuthorUnknown = "bugReport.authorUnknown";
    public const string ScreenshotUnknown = "bugReport.screenshotUnknown";
    public const string Unknown = "bugReport.unknown";
    public const string NothingToChange = "bugReport.nothingToChange";

    /// <summary>A report as it is stored — trimmed, blanks as none — or why it cannot be.</summary>
    public static (ReportBug? Report, string? Problem) Normalize(ReportBug report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var page = report.Page?.Trim() ?? "";
        var text = report.Text?.Trim() ?? "";
        var context = report.Context ?? new BugReportContext(null, null, [], []);
        if (page.Length is 0 or > MaxPageLength || !page.StartsWith('/') || page.StartsWith("//", StringComparison.Ordinal) || page.Any(char.IsControl))
        {
            return (null, $"A page is a path of this site (starting with «/»), 1–{MaxPageLength} characters.");
        }

        if (text.Length is 0 or > MaxTextLength)
        {
            return (null, $"A description is 1–{MaxTextLength} characters.");
        }

        EquatableArray<BugContextEntry> actions = [.. context.Actions];
        EquatableArray<BugContextEntry> errors = [.. context.Errors];
        if (actions.Count > MaxActions || errors.Count > MaxErrors
            || actions.Concat(errors).Any(e => e is null || e.Text is null || e.Text.Length > MaxEntryLength)
            || context.UserAgent?.Length > MaxUserAgentLength || context.Viewport?.Length > MaxViewportLength)
        {
            return (null, $"A context is at most {MaxActions} actions and {MaxErrors} errors of {MaxEntryLength} characters.");
        }

        return (report with
        {
            Page = page,
            Text = text,
            Context = new BugReportContext(Blank(context.UserAgent), Blank(context.Viewport), actions, errors),
        }, null);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
