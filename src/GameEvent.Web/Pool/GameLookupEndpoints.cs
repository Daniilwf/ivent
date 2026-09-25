using System.Security.Claims;
using GameEvent.Infrastructure.Files;
using GameEvent.Infrastructure.Pool.Metadata;
using GameEvent.Web.Accounts;
using GameEvent.Web.Files;
using GameEvent.Web.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        var settings = SettingsFrom(configuration);
        var services = builder.Services;
        services.AddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient("metadata", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("GameEvent/1.0 (game pool)");
        });
        // An address that is not HowLongToBeat's own leaves the hours to the admin (D-29)
        services.AddSingleton<IHoursProvider>(sp => settings.HltbUsable ? new HltbHoursProvider(Client(sp), settings) : new ManualHours());
        if (settings.SteamEnabled)
        {
            services.AddSingleton<ICoverProvider>(sp => new SteamCoverProvider(Client(sp), sp.GetRequiredService<ILogger<SteamCoverProvider>>()));
        }

        if (!string.IsNullOrEmpty(settings.IgdbClientId) && !string.IsNullOrEmpty(settings.IgdbClientSecret))
        {
            services.AddSingleton<ICoverProvider>(sp => new IgdbCoverProvider(Client(sp), settings, sp.GetRequiredService<TimeProvider>()));
        }

        services.AddSingleton(sp => new CoverDownloader(new SafeDownloader(
            sp.GetRequiredService<IHostResolver>(),
            new DownloadSettings { AllowedHosts = settings.CoverHosts, TimeoutSeconds = settings.TimeoutSeconds * 2 },
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

    /// <summary>
    /// <c>Metadata</c> (D-118): the IGDB keys also as <c>IGDB_CLIENT_ID</c>/<c>IGDB_CLIENT_SECRET</c>; a list of cover hosts
    /// in the configuration replaces the default one.
    /// </summary>
    internal static MetadataSettings SettingsFrom(IConfiguration configuration)
    {
        var section = configuration.GetSection("Metadata");
        var bound = section.Get<MetadataSettings>() ?? new MetadataSettings();
        return new MetadataSettings
        {
            TimeoutSeconds = bound.TimeoutSeconds,
            SteamEnabled = bound.SteamEnabled,
            HltbSearchUrl = bound.HltbSearchUrl,
            IgdbClientId = configuration["Metadata:IgdbClientId"] ?? configuration["IGDB_CLIENT_ID"],
            IgdbClientSecret = configuration["Metadata:IgdbClientSecret"] ?? configuration["IGDB_CLIENT_SECRET"],
            CoverHosts = section.GetSection("CoverHosts").Get<string[]>() is { Length: > 0 } hosts ? hosts : new MetadataSettings().CoverHosts,
        };
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
        var hoursTask = Safe("hltb", () => hours.FindHoursAsync(title, ct), unavailable, logger, ct);

        // A repeat of the same lookup keeps the cover it stored: nothing is downloaded or stored twice
        var owner = principal.UserId();
        var earlier = owner is { } id ? await EarlierCoverAsync(request.CommandId, id, files, ct) : null;
        CoverCandidate? cover = null;
        FileLinkView? stored = earlier is { } earlierFile ? FileLinkView.Of(earlierFile) : null;
        foreach (var provider in covers)
        {
            var candidate = await Safe(provider.Name, () => provider.FindCoverAsync(title, ct), unavailable, logger, ct);
            if (candidate is null)
            {
                continue;
            }

            cover ??= candidate;
            if (stored is not null || owner is null)
            {
                break;
            }

            // The picture, else its alternate; nothing downloadable — the next provider is asked
            foreach (var url in new[] { candidate.ImageUrl, candidate.AlternateImageUrl }.OfType<string>())
            {
                stored = await StoreCoverAsync(url, candidate.Source, request.CommandId, owner.Value, downloader, files, logger, ct);
                if (stored is not null)
                {
                    cover = candidate;
                    break;
                }
            }

            if (stored is not null)
            {
                break;
            }

            unavailable.Add(candidate.Source + "-cover");
        }

        var foundHours = await hoursTask;
        return TypedResults.Ok(new GameLookupView(
            foundHours, cover?.Year, cover?.SteamAppId, stored, stored is null ? null : cover?.Source, [.. unavailable.Distinct()]));
    }

    // Covers are the pool's, not the admin's own uploads: they do not use up his daily limit (D-118)
    private static async Task<FileLinkView?> StoreCoverAsync(
        string url, string source, Guid commandId, Guid owner, CoverDownloader downloader, FileServices files, ILogger logger, CancellationToken ct)
    {
        var (content, refused) = await downloader.Inner.DownloadAsync(url, ct);
        if (content is null)
        {
            LogCoverRefused(logger, source, refused?.Code ?? "?");
            return null;
        }

        var (answer, fileId) = await FileEndpoints.StoreFileAsync(content, commandId, owner, files, ct, countTowardsLimit: false);
        if (fileId is null)
        {
            LogCoverNotStored(logger, source, ((answer as Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)?.ProblemDetails.Extensions.TryGetValue("code", out var code) == true ? code?.ToString() : null) ?? "?");
        }

        return fileId is { } id ? FileLinkView.Of(id) : null;
    }

    private static async Task<Guid?> EarlierCoverAsync(Guid commandId, Guid owner, FileServices files, CancellationToken ct)
    {
        var earlier = await files.Db.Events.AsNoTracking()
            .Where(e => e.SeasonId == Guid.Empty && e.CommandId == commandId && e.AuthorId == owner && e.Type == "file-stored")
            .Select(e => new { e.Type, e.Version, e.Data })
            .FirstOrDefaultAsync(ct);
        return earlier is null
            ? null
            : (Engine.Kernel.EventCodec.Decode(new Engine.Kernel.StoredEvent(earlier.Type, earlier.Version, earlier.Data)) as Engine.Files.FileStored)?.FileId;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Game lookup: the {Source} cover was downloaded but not stored ({Code})")]
    private static partial void LogCoverNotStored(ILogger logger, string source, string code);

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
