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

    [Fact]
    public void A_browser_that_signed_in_to_the_login_before_is_not_stopped_by_the_per_login_ceiling()
    {
        // SEC-2: a guess spread over a few addresses must not lock the owner out of their own account
        var throttle = Throttle(perLogin: 10);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(throttle.TryBegin("petya", $"203.0.113.{i}"));
        }

        Assert.False(throttle.TryBegin("petya", "198.51.100.7"));
        Assert.True(throttle.TryBegin("petya", "198.51.100.7", knownDevice: true));
    }

    [Fact]
    public void A_known_browser_is_still_stopped_by_the_limit_of_its_own_address()
    {
        var throttle = Throttle();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(throttle.TryBegin("petya", "203.0.113.1", knownDevice: true));
        }

        Assert.False(throttle.TryBegin("petya", "203.0.113.1", knownDevice: true));
    }

    [Fact]
    public void Attempts_of_logins_never_tried_again_are_forgotten_after_their_windows()
    {
        // SEC-3: every typed login used to stay in memory for good, so a stream of made-up logins grew the site's memory
        var throttle = Throttle();
        for (var i = 0; i < 1000; i++)
        {
            throttle.TryBegin($"guess-{i}", $"203.0.113.{i % 200}");
        }

        _clock.UtcNow = _clock.UtcNow.AddMinutes(61);
        throttle.TryBegin("petya", "198.51.100.7");

        Assert.True(throttle.TrackedKeys <= 2, $"{throttle.TrackedKeys} keys are still tracked.");
    }

    [Fact]
    public void The_per_login_ceiling_outlives_a_sweep_after_the_address_window()
    {
        // SEC-3 review: a sweep by the shorter address window would drop the ceiling's counts after 15 minutes, not 60
        var throttle = Throttle(perLogin: 10);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(throttle.TryBegin("petya", $"203.0.113.{i}"));
        }

        _clock.UtcNow = _clock.UtcNow.AddMinutes(25);
        throttle.TryBegin("vasya", "198.51.100.1"); // the sweep runs

        Assert.False(throttle.TryBegin("petya", "203.0.113.200"));

        _clock.UtcNow = _clock.UtcNow.AddMinutes(36);
        Assert.True(throttle.TryBegin("petya", "203.0.113.200"));
    }
}
