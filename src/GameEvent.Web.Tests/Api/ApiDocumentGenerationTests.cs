using GameEvent.Infrastructure.Queue;
using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Hosting;
using GameEvent.Web.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The build-time OpenAPI generator (<c>npm run gen:api</c>) starts the app over an empty, unmigrated database and fails
/// on any logged error. A hosted service that read the database there — the queue settling what was left pending at its
/// start (D-702) — logged «no such table: Season» whenever it ran before the generator stopped the host (FL-4, D-760).
/// </summary>
public sealed class ApiDocumentGenerationTests
{
    private static readonly Type[] s_hostedServices =
        [typeof(SiteLockService), typeof(SeasonBroadcastWorker), typeof(CommandProcessor), typeof(DeadlineScheduler)];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_generator_starts_no_hosted_service(bool generatingApiDocument)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration["ConnectionStrings:Main"] = "Data Source=api-document.db";
        builder.Configuration["Site:LockFile"] = "api-document.lock";

        AppSetup.AddGameEvent(builder, generatingApiDocument);

        var registered = builder.Services
            .Where(s => s.ServiceType == typeof(IHostedService))
            .Select(s => s.ImplementationType)
            .ToList();
        foreach (var service in s_hostedServices)
        {
            Assert.Equal(!generatingApiDocument, registered.Contains(service));
        }
    }
}
