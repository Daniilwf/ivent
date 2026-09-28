using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.BugReports;

// The global log's bug reports (SPEC «Кнопка „Сообщить о баге“», E5, D-121): a report is what the user sent, as sent;
// the admin moves it through its statuses. They belong to the site, not to a season.

/// <summary>Where a bug report is (GLOSSARY «Статус отчёта о баге»): new, taken into work (in docs/BUGS.md), closed.</summary>
public enum BugReportStatus
{
    New,
    InWork,
    Closed,
}

/// <summary>One line of a report's context: when (by the browser's clock) and what.</summary>
public sealed record BugContextEntry(DateTimeOffset? At, string Text);

/// <summary>
/// What the page gathered by itself (GLOSSARY «Контекст отчёта»): the last actions (clicks, requests with their answers),
/// the browser's errors, the browser and the window size. Never what the user typed into fields.
/// </summary>
public sealed record BugReportContext(
    string? UserAgent,
    string? Viewport,
    EquatableArray<BugContextEntry> Actions,
    EquatableArray<BugContextEntry> Errors);

/// <summary>A user sent a bug report from a page; <see cref="ScreenshotFileId"/> — their own screenshot of it, if any.</summary>
[EventType("bug-reported")]
public sealed record BugReported(Guid ReportId, Guid AuthorId, string Page, string Text, BugReportContext Context, Guid? ScreenshotFileId) : IGameEvent;

/// <summary>The admin moved a report to another status.</summary>
[EventType("bug-report-status-changed")]
public sealed record BugReportStatusChanged(Guid ReportId, BugReportStatus Status) : IGameEvent;
