using System.Reflection;

namespace GameEvent.Web.Site;

/// <summary>
/// The version the site runs (J4, D-201): <c>Site:Version</c> — the release tag the image was built from
/// (<c>docker build --build-arg VERSION=v1.2.0</c>) — or, without it, the assembly's own version with the build
/// metadata cut off («1.0.0», a local run).
/// </summary>
public static class SiteVersion
{
    public static string Of(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration["Site:Version"] is { Length: > 0 } configured)
        {
            return configured;
        }

        var informational = typeof(SiteVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational?.Split('+')[0] ?? "dev";
    }
}
