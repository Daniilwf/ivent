using System.Security.Claims;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Pool.Metadata;
using GameEvent.Web.Accounts;
using GameEvent.Web.Files;
using GameEvent.Web.Hosting;

namespace GameEvent.Web.Pool;

/// <summary>What the admin looks up for a game by its title (D-118).</summary>
public sealed record GameLookupRequest(Guid CommandId, string? Title);

/// <summary>
/// What the external bases say about a game (SPEC «Обложки игр», «Длина игры», D-28, D-29, D-118): the main story hours
/// (HowLongToBeat, when on), the release year and Steam app, and the cover — already downloaded and stored on our server.
/// Anything not found is null; <c>unavailable</c> names the services that failed, so the admin knows to fill it by hand.
/// </summary>
public sealed record GameLookupView(
    decimal? Hours, int? Year, string? SteamAppId, FileLinkView? Cover, string? CoverSource, IReadOnlyList<string> Unavailable);

/// <summary>A cover's picture comes from a service's answer: it is downloaded through the same guard as avatars, with the covers' hosts.</summary>
public sealed class CoverDownloader(SafeDownloader inner)
{
    public SafeDownloader Inner { get; } = inner;
}

/// <summary>
/// The pool's helpers from outside (D-118): every service is optional and can fail; a failure is «no data», never an error
/// of the site — the admin types what is missing (SPEC «Трудности реализации»: HowLongToBeat, the site works without it).
/// </summary>
public static partial class GameLookupEndpoints
{
    public const int MaxTitleLength = 200;

    public static void AddGameLookup(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configuration = builder.Configuration;
        var settings = (configuration.GetSection("Metadata").Get<MetadataSettings>() ?? new MetadataSettings()) with
        {
            IgdbClientId = configuration["Metadata:IgdbClientId"] ?? configuration["IGDB_CLIENT_ID"],
            IgdbClientSecret = configuration["Metadata:IgdbClientSecret"] ?? configuration["IGDB_CLIENT_SECRET"],
        };
        var services = builder.Services;
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TimeProviderClock>();
        services.AddHttpClient("metadata", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("GameEvent/1.0 (game pool)");
        });
        services.AddSingleton<IHoursProvider>(sp => string.IsNullOrEmpty(settings.HltbSearchUrl)
            ? new ManualHours()
            : new HltbHoursProvider(Client(sp), settings));
        if (settings.SteamEnabled)
        {
            services.AddSingleton<ICoverProvider>(sp => new SteamCoverProvider(Client(sp), sp.GetRequiredService<ILogger<SteamCoverProvider>>()));
        }

        if (!string.IsNullOrEmpty(settings.IgdbClientId) && !string.IsNullOrEmpty(settings.IgdbClientSecret))
        {
            services.AddSingleton<ICoverProvider>(sp => new IgdbCoverProvider(Client(sp), settings, sp.GetRequiredService<TimeProviderClock>()));
        }

        services.AddSingleton(sp => new CoverDownloader(new SafeDownloader(
            sp.GetRequiredService<IHostResolver>(),
            new DownloadSettings { AllowedHosts = [.. settings.CoverHosts.Select(h => "*." + h)], TimeoutSeconds = settings.TimeoutSeconds * 2 },
            sp.GetRequiredService<FileLimits>())));
    }

    public static void MapGameLookup(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPost("/admin/pool/lookup", LookupAsync)
            .WithTags("Admin")
            .RequireAuthorization(Policies.Admin)
            .RequireRateLimiting(AppSetup.AdminReadRateLimit)
            .Produces<GameLookupView>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static HttpClient Client(IServiceProvider sp) => sp.GetRequiredService<IHttpClientFactory>().CreateClient("metadata");

    private static async Task<IResult> LookupAsync(
        GameLookupRequest request,
        ClaimsPrincipal principal,
        IHoursProvider hours,
        IEnumerable<ICoverProvider> covers,
        CoverDownloader downloader,
        [AsParameters] FileServices files,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var logger = loggers.CreateLogger(typeof(GameLookupEndpoints));
        if (request.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitleLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["title"] = [$"A command id and a title of up to {MaxTitleLength} characters are required."],
            });
        }

        var title = request.Title.Trim();
        var unavailable = new List<string>();

        // Both lookups at once; each one's failure is its own
        var hoursTask = Safe("hltb", () => hours.FindHoursAsync(title, ct), unavailable, logger, ct);
        CoverCandidate? cover = null;
        foreach (var provider in covers)
        {
            cover = await Safe(provider.Name, () => provider.FindCoverAsync(title, ct), unavailable, logger, ct);
            if (cover is not null)
            {
                break;
            }
        }

        var foundHours = await hoursTask;
        FileLinkView? stored = null;
        if (cover is not null && principal.UserId() is { } admin)
        {
            var (content, refused) = await downloader.Inner.DownloadAsync(cover.ImageUrl, ct);
            if (content is not null)
            {
                var (_, fileId) = await FileEndpoints.StoreFileAsync(content, request.CommandId, admin, files, ct);
                stored = fileId is { } id ? FileLinkView.Of(id) : null;
            }
            else
            {
                LogCoverRefused(logger, cover.Source, refused?.Code ?? "?");
            }

            if (stored is null)
            {
                unavailable.Add(cover.Source + "-cover");
            }
        }

        return TypedResults.Ok(new GameLookupView(
            foundHours, cover?.Year, cover?.SteamAppId, stored, stored is null ? null : cover?.Source, [.. unavailable.Distinct()]));
    }

    private static async Task<T?> Safe<T>(string service, Func<Task<T?>> lookup, List<string> unavailable, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await lookup();
        }
#pragma warning disable CA1031 // an outside service may fail any way it likes (a timeout included): «no data», and the site goes on
        catch (Exception e) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            lock (unavailable)
            {
                unavailable.Add(service);
            }

            LogServiceFailed(logger, service, e.GetType().Name, e.Message);
            return default;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Game lookup: {Service} failed ({Type}: {Reason}); the admin fills it by hand")]
    private static partial void LogServiceFailed(ILogger logger, string service, string type, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Game lookup: the {Source} cover was not downloaded ({Code})")]
    private static partial void LogCoverRefused(ILogger logger, string source, string code);
}
