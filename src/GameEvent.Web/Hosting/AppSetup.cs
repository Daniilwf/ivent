using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using GameEvent.Engine.Kernel;
using GameEvent.Infrastructure.Database;
using GameEvent.Infrastructure.Kernel;
using GameEvent.Infrastructure.Queue;
using GameEvent.Web.Accounts;
using GameEvent.Web.Proofs;
using GameEvent.Web.Realtime;
using GameEvent.Web.Rolls;
using GameEvent.Web.Rulesets;
using GameEvent.Web.Runs;
using GameEvent.Web.Seasons;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace GameEvent.Web.Hosting;

/// <summary>Services and pipeline of the site. Tests replace clock and random through the service collection.</summary>
public static class AppSetup
{
    public const string LoginRateLimit = "login";

    /// <summary>Admin reads that fold the whole season log: per user, a few per second at most (D-92).</summary>
    public const string AdminReadRateLimit = "admin-read";

    public const int AdminReadsPerMinute = 30;

    public static void AddGameEvent(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        var connectionString = ResolveDataSource(builder.Configuration.ConnectionString(), builder.Environment.ContentRootPath);
        builder.Configuration["ConnectionStrings:Main"] = connectionString;

        services.AddDbContextFactory<GameEventDbContext>(o => SqliteDatabase.Configure(o, connectionString));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IRandomSource, CryptoRandomSource>();
        services.AddSingleton<IIdGenerator, GuidV7Ids>();
        services.AddSingleton<CommandBus>();
        services.AddSingleton<SeasonBroadcaster>();
        services.AddSingleton<ICommittedEventsListener>(sp => sp.GetRequiredService<SeasonBroadcaster>());
        services.AddHostedService<SeasonBroadcastWorker>();
        services.AddHostedService<CommandProcessor>();

        services.Configure<JsonOptions>(o => ConfigureJson(o.SerializerOptions));
        services.AddSignalR().AddJsonProtocol(o => ConfigureJson(o.PayloadSerializerOptions));
        services.AddProblemDetails();
        services.AddOpenApi(o => o.AddDocumentTransformer(async (document, context, ct) =>
        {
            // Hub messages are part of the contract too: the frontend gets their types from the same document.
            var schema = await context.GetOrCreateSchemaAsync(typeof(SeasonUpdate), null, ct);
            document.Components ??= new();
            document.Components.Schemas ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSchema>();
            document.Components.Schemas[nameof(SeasonUpdate)] = schema;
        }));

        // Local http only in Development and Test; everywhere else (staging too) cookies are Secure and __Host-.
        var local = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Test");
        var cookiePrefix = local ? "" : "__Host-";
        var securePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

        services.AddSingleton<Passwords>();
        services.AddSingleton<LoginThrottle>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = cookiePrefix + "ge.session";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = securePolicy;
                o.Cookie.Path = "/";
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
                o.Events.OnValidatePrincipal = WebSecurity.ValidateSessionAsync;
                // An API answers with status codes, never redirects to a login page.
                o.Events.OnRedirectToLogin = ctx => Status(ctx.Response, StatusCodes.Status401Unauthorized);
                o.Events.OnRedirectToAccessDenied = ctx => Status(ctx.Response, StatusCodes.Status403Forbidden);
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Player, p => p.RequireRole(nameof(Infrastructure.Accounts.Role.Player)))
            .AddPolicy(Policies.Admin, p => p.RequireRole(nameof(Infrastructure.Accounts.Role.Admin)));

        services.AddAntiforgery(o =>
        {
            o.HeaderName = Csrf.HeaderName;
            o.Cookie.Name = cookiePrefix + "ge.csrf";
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = securePolicy;
            o.Cookie.Path = "/";
        });

        var attempts = builder.Configuration.GetValue("Security:LoginAttemptsPerMinute", 10);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                WebSecurity.ClientKey(ctx.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = attempts, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(AdminReadRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.UserId()?.ToString() ?? WebSecurity.ClientKey(ctx.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = AdminReadsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        services.Configure<ForwardedHeadersOptions>(o => WebSecurity.ConfigureForwardedHeaders(o, builder.Configuration));
    }

    public static void UseGameEvent(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseForwardedHeaders();
        if (app.Environment.IsProduction() && app.Configuration.GetSection("Proxy:KnownNetworks").Get<string[]>() is not { Length: > 0 })
        {
            // Behind Caddy without a trusted proxy network every client looks the same: the login limit becomes shared.
            app.Logger.LogWarning("Proxy:KnownNetworks is empty: client addresses come from the proxy, login limits are shared by everyone.");
        }

        app.UseSecurityHeaders();
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Test"))
        {
            app.UseHsts();
        }

        app.UseApiBodyLimit();
        var frontend = app.UseFrontendFiles();

        // Explicit routing after the static files: otherwise the SPA fallback endpoint is chosen first
        // and the static file middleware skips requests for /assets/*.js.
        app.UseRouting();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Test"))
        {
            app.MapOpenApi();
        }

        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
        var api = app.MapGroup("/api").AddEndpointFilter<Csrf.Filter>();
        api.MapAccounts();
        api.MapSeasons();
        api.MapRules();
        api.MapPoolStats();
        api.MapAdminRuns();
        api.MapAdminProofs();
        app.MapHub<SeasonHub>(SeasonHub.Path);

        if (frontend is not null)
        {
            // Client-side routes get the SPA; unknown API and hub paths stay 404.
            app.MapFallbackToFile("{*path:regex(^(?!(api|hubs)(/|$)).*$)}", "index.html", new StaticFileOptions { FileProvider = frontend });
        }
    }

    /// <summary>
    /// Serves the built frontend (SPEC: one process serves everything). The path is <c>Frontend:DistPath</c>,
    /// relative to the content root; by default the Vite build output in <c>web/dist</c>. Absent — API only.
    /// </summary>
    private static Microsoft.Extensions.FileProviders.PhysicalFileProvider? UseFrontendFiles(this WebApplication app)
    {
        var configured = app.Configuration["Frontend:DistPath"] ?? Path.Combine("..", "..", "web", "dist");
        var path = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, configured));
        if (!File.Exists(Path.Combine(path, "index.html")))
        {
            if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Test"))
            {
                app.Logger.LogWarning("No built frontend at {Path}: the site serves the API only.", path);
            }

            return null;
        }

        var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(path);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        return files;
    }

    public static string ConnectionString(this IConfiguration configuration) =>
        configuration.GetConnectionString("Main")
        ?? throw new InvalidOperationException("ConnectionStrings:Main is not set.");

    /// <summary>A relative SQLite file path is taken relative to the content root, not the working directory.</summary>
    public static string ResolveDataSource(string connectionString, string contentRoot)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(builder.DataSource) && builder.DataSource != ":memory:" && !Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(contentRoot, builder.DataSource));
        }

        return builder.ConnectionString;
    }

    /// <summary>Applies migrations; a separate deployment step (D-27), never on startup.</summary>
    public static async Task MigrateAsync(IConfiguration configuration)
    {
        var connectionString = configuration.ConnectionString();
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await SqliteDatabase.MigrateAsync(connectionString);
    }

    private static void ConfigureJson(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.RespectRequiredConstructorParameters = true;
        options.RespectNullableAnnotations = true;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    }

    private static Task Status(HttpResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}

public static class Policies
{
    public const string Player = "player";
    public const string Admin = "admin";
}

/// <summary>
/// CSRF protection for the SPA (D-26): every state-changing API call carries the antiforgery token in a header.
/// SameSite=Strict cookies are the first line; the token is the second.
/// </summary>
public static class Csrf
{
    public const string HeaderName = "X-CSRF-TOKEN";

    public sealed class Filter(Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);
            var http = context.HttpContext;
            if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
            {
                return await next(context);
            }

            if (!await antiforgery.IsRequestValidAsync(http))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing or invalid antiforgery token.",
                    extensions: new Dictionary<string, object?> { ["code"] = "csrf.invalid" });
            }

            return await next(context);
        }
    }
}
