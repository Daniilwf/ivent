using System.Text.Json;
using GameEvent.Engine.BugReports;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.BugReports;
using GameEvent.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Infrastructure.Queue;

/// <summary>
/// Bug report commands (D-121): a report from an active account with its own screenshot, and the admin's status changes.
/// </summary>
public sealed partial class CommandProcessor
{
    private async Task<(IReadOnlyList<IGameEvent> Events, Action? Apply, string? Secret, Rejection? Rejection)> DecideBugReportAsync(
        GameEventDbContext db, IGlobalCommand command, DateTimeOffset now, CancellationToken ct)
    {
        static (IReadOnlyList<IGameEvent>, Action?, string?, Rejection?) Reject(string code, string detail) => ([], null, null, new Rejection(code, detail));

        switch (command)
        {
            case ReportBug report:
                {
                    var (normalized, problem) = BugReportRules.Normalize(report);
                    if (normalized is null)
                    {
                        return Reject(BugReportRules.Invalid, problem!);
                    }

                    if (!await db.Users.AnyAsync(u => u.Id == normalized.AuthorId && !u.IsDeleted, ct))
                    {
                        return Reject(BugReportRules.AuthorUnknown, "The author is not an active account.");
                    }

                    if (normalized.ScreenshotFileId is { } file
                        && !await db.Files.AnyAsync(f => f.Id == file && f.OwnerId == normalized.AuthorId && !f.IsDeleted, ct))
                    {
                        return Reject(BugReportRules.ScreenshotUnknown, "The screenshot is one of the author's own uploads.");
                    }

                    var id = ids.NewId();
                    var record = new BugReportRecord
                    {
                        Id = id,
                        AuthorId = normalized.AuthorId,
                        Page = normalized.Page,
                        Text = normalized.Text,
                        ContextJson = JsonSerializer.Serialize(normalized.Context, EngineJson.Options),
                        ScreenshotFileId = normalized.ScreenshotFileId,
                        Status = BugReportStatus.New,
                        CreatedAt = now,
                    };
                    return (
                        [new BugReported(id, normalized.AuthorId, normalized.Page, normalized.Text, normalized.Context, normalized.ScreenshotFileId)],
                        () => db.BugReports.Add(record),
                        null,
                        null);
                }

            case SetBugReportStatus status:
                {
                    if (!Enum.IsDefined(status.Status))
                    {
                        return Reject(BugReportRules.Invalid, "Unknown status.");
                    }

                    if (await db.BugReports.SingleOrDefaultAsync(r => r.Id == status.ReportId, ct) is not { } record)
                    {
                        return Reject(BugReportRules.Unknown, $"Bug report {status.ReportId} does not exist.");
                    }

                    return record.Status == status.Status
                        ? Reject(BugReportRules.NothingToChange, "The report already has this status.")
                        : ([new BugReportStatusChanged(record.Id, status.Status)], () => record.Status = status.Status, null, null);
                }

            default:
                throw new InvalidOperationException($"Unknown bug report command {command.GetType().Name}.");
        }
    }
}
