using GameEvent.Web.Accounts;
using GameEvent.Web.Tests.Queue;
using Microsoft.Extensions.Configuration;

namespace GameEvent.Web.Tests.Api;

/// <summary>Per login and address the guesser is stopped; the owner from another address still gets in.</summary>
public class LoginThrottleTests
{
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private LoginThrottle Throttle(int perLogin = 50) => new(_clock, new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:LoginFailuresPerLogin"] = perLogin.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        .Build());

    [Fact]
    public void Five_failures_from_one_address_block_that_address_only()
    {
        var throttle = Throttle();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(throttle.TryBegin("petya", "203.0.113.1"));
        }

        Assert.False(throttle.TryBegin("petya", "203.0.113.1"));
        Assert.True(throttle.TryBegin("petya", "198.51.100.7")); // the owner at home
    }

    [Fact]
    public void Block_ends_after_the_window()
    {
        var throttle = Throttle();
        for (var i = 0; i < 5; i++)
        {
            throttle.TryBegin("petya", "203.0.113.1");
        }

        _clock.UtcNow = _clock.UtcNow.AddMinutes(16);

        Assert.True(throttle.TryBegin("petya", "203.0.113.1"));
    }

    [Fact]
    public void Guess_spread_over_many_addresses_hits_the_per_login_ceiling()
    {
        var throttle = Throttle(perLogin: 10);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(throttle.TryBegin("petya", $"203.0.113.{i}"));
        }

        Assert.False(throttle.TryBegin("petya", "203.0.113.200"));
    }

    [Fact]
    public void Success_frees_the_reserved_attempt()
    {
        var throttle = Throttle();
        for (var i = 0; i < 4; i++)
        {
            throttle.TryBegin("petya", "203.0.113.1");
        }

        throttle.TryBegin("petya", "203.0.113.1");
        throttle.Succeeded("petya", "203.0.113.1");

        Assert.True(throttle.TryBegin("petya", "203.0.113.1"));
    }
}
