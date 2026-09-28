namespace GameEvent.Web.Site;

/// <summary>
/// Which copy of the site this is (H9, D-220): every page shows a strip on anything but the live site, so nobody takes the
/// test copy for the real one. <c>Other</c> — an environment name the site does not know.
/// </summary>
public enum SiteEnvironment
{
    Production,
    Staging,
    Development,
    Test,
    Other,
}

public static class SiteEnvironments
{
    public static SiteEnvironment Of(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.EnvironmentName switch
        {
            var name when string.Equals(name, Environments.Production, StringComparison.OrdinalIgnoreCase) => SiteEnvironment.Production,
            var name when string.Equals(name, Environments.Staging, StringComparison.OrdinalIgnoreCase) => SiteEnvironment.Staging,
            var name when string.Equals(name, Environments.Development, StringComparison.OrdinalIgnoreCase) => SiteEnvironment.Development,
            var name when string.Equals(name, "Test", StringComparison.OrdinalIgnoreCase) => SiteEnvironment.Test,
            _ => SiteEnvironment.Other,
        };
    }
}
