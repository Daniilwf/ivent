using System.Text.Json;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using Serilog.Events;

namespace GameEvent.Web.Observability;

/// <summary>A health check's answer: the overall status and one status per check, no details (D-107).</summary>
public sealed record HealthView(string Status, IReadOnlyDictionary<string, string> Checks);

/// <summary>
/// Observability (SPEC «Наблюдаемость», A10, D-107): Serilog to rotating files, the journal of unhandled exceptions for
/// the admin's «Ошибки» page, and <c>/health</c> over the database, the disk and the command queue.
/// </summary>
public static class ObservabilitySetup
{
    public static void AddObservability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configuration = builder.Configuration;
        var logPath = Path.GetFullPath(Path.Combine(
            builder.Environment.ContentRootPath, configuration["Logging:File:Path"] ?? Path.Combine("var", "logs", "game-event-.log")));
        var toFile = configuration.GetValue("Logging:File:Enabled", true);
        builder.Services.AddSerilog((_, logger) =>
        {
            // Levels come from Logging:LogLevel (appsettings or environment): raised in production without a rebuild
            logger.MinimumLevel.Is(Level(configuration["Logging:LogLevel:Default"], LogEventLevel.Information));
            foreach (var source in configuration.GetSection("Logging:LogLevel").GetChildren().Where(c => c.Key != "Default"))
            {
                logger.MinimumLevel.Override(source.Key, Level(source.Value, LogEventLevel.Warning));
            }

            logger
                .Enrich.FromLogContext()
                .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture);
            if (toFile)
            {
                // A file a day and at most 20 MB each, the last 30 kept (a season's worth of evenings)
                logger.WriteTo.File(
                    logPath,
                    formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 20L * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 30,
                    shared: true);
            }
        }, preserveStaticLogger: true); // each host owns its logger: nothing global to close under another host

        builder.Services.AddSingleton<ErrorJournal>();
        builder.Services.AddExceptionHandler<JournalExceptionHandler>();

        // The journal's handler already logs each exception with its error id: the middleware does not log it again
        builder.Services.Configure<ExceptionHandlerOptions>(o => o.SuppressDiagnosticsCallback = _ => true);
        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database")
            .AddCheck<DiskHealthCheck>("disk")
            .AddCheck<QueueHealthCheck>("queue");
    }

    public static void MapObservability(this WebApplication app, RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(api);

        // Public and anonymous (the proxy and the deploy check it): statuses only, the reasons stay in the log
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = (http, report) =>
            {
                http.Response.ContentType = "application/json";
                var view = new HealthView(
                    Camel(report.Status),
                    report.Entries.ToDictionary(e => e.Key, e => Camel(e.Value.Status)));
                return http.Response.WriteAsync(JsonSerializer.Serialize(view, JsonSerializerOptions.Web));
            },
        }).ExcludeFromDescription();

        api.MapGet("/admin/errors", Ok<IReadOnlyList<ErrorEntry>> (ErrorJournal journal) => TypedResults.Ok(journal.Latest()))
            .WithTags("Admin")
            .RequireAuthorization(Policies.Admin)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Only the Test environment: a request that fails, to see the journal and the log at work (A7)
        if (app.Environment.IsEnvironment("Test"))
        {
            api.MapGet("/test/fail", IResult () => throw new InvalidOperationException("A test failure: nothing is wrong."))
                .ExcludeFromDescription();
        }
    }

    // Serilog has no "off": a level above Fatal lets nothing through
    private const LogEventLevel Off = LogEventLevel.Fatal + 1;

    /// <summary>A level of Microsoft logging in the config, as Serilog names it; an unknown one keeps the fallback.</summary>
    internal static LogEventLevel Level(string? name, LogEventLevel fallback) => name switch
    {
        "Trace" => LogEventLevel.Verbose,
        "Debug" => LogEventLevel.Debug,
        "Information" => LogEventLevel.Information,
        "Warning" => LogEventLevel.Warning,
        "Error" => LogEventLevel.Error,
        "Critical" => LogEventLevel.Fatal,
        "None" => Off,
        _ => fallback,
    };

    private static string Camel(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "healthy",
        HealthStatus.Degraded => "degraded",
        _ => "unhealthy",
    };
}
