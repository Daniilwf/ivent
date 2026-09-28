using System.Security.Claims;
using GameEvent.Infrastructure.Site;
using GameEvent.Web.Accounts;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GameEvent.Web.Site;

/// <summary>
/// What every page asks the site: whether it only reads now (the maintenance banner) and which version it runs — a page
/// that saw another version before shows «Что нового» (J4, D-201). <c>environment</c> — which copy of the site this is
/// (anything but <c>production</c> shows a strip on every page); <c>testTools</c> — the test endpoints are there (only
/// Development and Test), so the admin gets the page of the test tools (H9, D-220).
/// </summary>
public sealed record SiteStatusView(bool Maintenance, string Version, SiteEnvironment Environment, bool TestTools);

public sealed record MaintenanceRequest(bool On);

/// <summary>
/// Maintenance mode (SPEC «Режим обслуживания», A8, D-121): while the flag file is there every write to the API is 503
/// with the code <see cref="MaintenanceMode.Code"/> and the queue refuses every command. Signing in and out and turning
/// the mode off stay open, so the admin can end it from the site; reading, files and the hub stay as they are.
/// </summary>
public static partial class MaintenanceEndpoints
{
    /// <summary>How soon a client may try again: a deploy's maintenance lasts about a minute (SPEC «Выкладка ночью»).</summary>
    public const int RetryAfterSeconds = 60;

    private static readonly string[] s_openWrites = ["/api/auth/login", "/api/auth/logout", "/api/admin/maintenance"];

    /// <summary>The flag is <c>Maintenance:FlagPath</c>, by default the file <c>maintenance</c> next to the database.</summary>
    public static void AddMaintenance(this WebApplicationBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configured = builder.Configuration["Maintenance:FlagPath"];
        var database = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        var path = !string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(builder.Environment.ContentRootPath, configured)
            : Path.IsPathRooted(database)
                ? Path.Combine(Path.GetDirectoryName(database)!, "maintenance")
                : Path.Combine(builder.Environment.ContentRootPath, "var", "maintenance");
        builder.Services.AddSingleton(new MaintenanceMode(path));
    }

    /// <summary>Refuses the API's writes while the site only reads.</summary>
    public static void UseMaintenance(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var maintenance = app.Services.GetRequiredService<MaintenanceMode>();
        app.Use(async (http, next) =>
        {
            if (!IsWrite(http.Request) || !maintenance.IsOn)
            {
                await next(http);
                return;
            }

            http.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await TypedResults.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The site is under maintenance.",
                    detail: "The site only reads for about a minute; try again then.",
                    extensions: new Dictionary<string, object?> { ["code"] = MaintenanceMode.Code })
                .ExecuteAsync(http);
        });
    }

    public static void MapMaintenance(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/status", (MaintenanceMode maintenance, IConfiguration configuration, IWebHostEnvironment environment) =>
                TypedResults.Ok(StatusOf(maintenance, configuration, environment)))
            .WithTags("Site")
            .AllowAnonymous();

        api.MapPut("/admin/maintenance", Set)
            .WithTags("Admin")
            .RequireAuthorization(Policies.Admin);
    }

    private static Results<Ok<SiteStatusView>, ProblemHttpResult> Set(
        MaintenanceRequest request,
        MaintenanceMode maintenance,
        ClaimsPrincipal user,
        ILoggerFactory loggers,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        try
        {
            if (request.On)
            {
                maintenance.TurnOn();
            }
            else
            {
                maintenance.TurnOff();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "The maintenance flag could not be changed.",
                detail: $"Check the rights on {maintenance.FlagPath}.",
                extensions: new Dictionary<string, object?> { ["code"] = "site.maintenanceFlag" });
        }

        // Not a command (the queue is closed while it is on): the site's log keeps who turned it on and off
        LogChanged(loggers.CreateLogger(typeof(MaintenanceEndpoints)), request.On, user.UserId());
        return TypedResults.Ok(StatusOf(maintenance, configuration, environment));
    }

    private static SiteStatusView StatusOf(MaintenanceMode maintenance, IConfiguration configuration, IWebHostEnvironment environment) =>
        new(maintenance.IsOn, SiteVersion.Of(configuration), SiteEnvironments.Of(environment), Testing.TestEndpoints.Available(environment));

    private static bool IsWrite(HttpRequest request) =>
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        && request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !s_openWrites.Any(open => string.Equals(request.Path.Value?.TrimEnd('/'), open, StringComparison.OrdinalIgnoreCase));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Maintenance mode turned {On} by {UserId}")]
    private static partial void LogChanged(ILogger logger, bool on, Guid? userId);
}
