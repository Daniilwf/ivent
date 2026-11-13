using System.Runtime.CompilerServices;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// A disposed test site leaves memory (D-665): a site kept alive by something that outlives it — the rate limiter's
/// timer loop did — makes every later test pay the garbage collector for all the sites before it.
/// </summary>
public sealed class SiteFactoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_disposed_site_is_collected()
    {
        var sites = new List<WeakReference>();
        for (var i = 0; i < 3; i++)
        {
            sites.Add(await UseAndDisposeAsync());
        }

        for (var i = 0; i < 10 && sites.Any(s => s.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(100, Ct);
        }

        Assert.All(sites, s => Assert.False(s.IsAlive));
    }

    // Not inlined: no local of this method keeps the site
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> UseAndDisposeAsync()
    {
        var site = new SiteFactory();
        var services = new WeakReference(site.Services);
        await site.SeedAsync();
        using (var vasya = await site.SignedInAsync("vasya"))
        {
            using var response = await vasya.GetAsync(new Uri($"/api/seasons/{SiteFactory.SeasonId}", UriKind.Relative), Ct);
            response.EnsureSuccessStatusCode();
        }

        await site.DisposeAsync();
        return services;
    }
}
