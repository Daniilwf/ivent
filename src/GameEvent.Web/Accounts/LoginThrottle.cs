using GameEvent.Engine.Kernel;

namespace GameEvent.Web.Accounts;

/// <summary>
/// Limits failed sign-ins (D-26, D-67). The hard limit is per login and client address: a guesser is stopped,
/// but cannot lock the owner out from elsewhere. A much higher per-login ceiling slows a guess spread over
/// many addresses; a browser that signed in to the login before is not held by it (SEC-2, D-460), so a guess from
/// elsewhere cannot lock the owner out either. Windows are temporary; nobody can lock an account for good.
/// An attempt is reserved before the password is checked, so parallel requests cannot slip past the limit.
/// Attempts past their windows are swept out about once a minute: a stream of made-up logins does not grow the
/// site's memory (SEC-3).
/// </summary>
public sealed class LoginThrottle(IClock clock, IConfiguration configuration)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);

    private readonly int _perAddress = configuration.GetValue("Security:LoginFailuresPerAddress", 5);
    private readonly TimeSpan _addressWindow = TimeSpan.FromMinutes(configuration.GetValue("Security:LoginFailureWindowMinutes", 15));
    private readonly int _perLogin = configuration.GetValue("Security:LoginFailuresPerLogin", 50);
    private readonly TimeSpan _loginWindow = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan s_sweepInterval = TimeSpan.FromMinutes(1);
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    /// <summary>
    /// Reserves an attempt; false means the caller is throttled and must not check the password. <paramref name="knownDevice"/>
    /// — the browser signed in to this login before (its device cookie, D-460): only its own address limit holds it.
    /// </summary>
    public bool TryBegin(string normalizedLogin, string clientKey, bool knownDevice = false)
    {
        lock (_gate)
        {
            SweepIfDue();
            var pair = Pair(normalizedLogin, clientKey);
            if (Count(pair, _addressWindow) >= _perAddress || (!knownDevice && Count(normalizedLogin, _loginWindow) >= _perLogin))
            {
                return false;
            }

            Add(pair);
            Add(normalizedLogin);
            return true;
        }
    }

    /// <summary>The password was right: the reserved attempt is not a failure, and this address starts afresh.</summary>
    public void Succeeded(string normalizedLogin, string clientKey)
    {
        lock (_gate)
        {
            _attempts.Remove(Pair(normalizedLogin, clientKey));
            if (_attempts.TryGetValue(normalizedLogin, out var all))
            {
                all.RemoveAt(all.Count - 1);
                if (all.Count == 0)
                {
                    _attempts.Remove(normalizedLogin);
                }
            }
        }
    }

    /// <summary>How many logins and login-address pairs have attempts in memory (for tests).</summary>
    internal int TrackedKeys
    {
        get
        {
            lock (_gate)
            {
                return _attempts.Count;
            }
        }
    }

    private void SweepIfDue()
    {
        var now = clock.UtcNow;
        if (now - _lastSweep < s_sweepInterval)
        {
            return;
        }

        _lastSweep = now;
        var oldest = now - (_addressWindow > _loginWindow ? _addressWindow : _loginWindow);
        foreach (var key in _attempts.Where(a => a.Value.Count == 0 || a.Value[^1] <= oldest).Select(a => a.Key).ToList())
        {
            _attempts.Remove(key);
        }
    }

    private static string Pair(string login, string clientKey) => login + "|" + clientKey;

    private int Count(string key, TimeSpan window)
    {
        if (!_attempts.TryGetValue(key, out var times))
        {
            return 0;
        }

        times.RemoveAll(t => t <= clock.UtcNow - window);
        if (times.Count == 0)
        {
            _attempts.Remove(key);
        }

        return times.Count;
    }

    private void Add(string key)
    {
        if (!_attempts.TryGetValue(key, out var times))
        {
            times = [];
            _attempts[key] = times;
        }

        times.Add(clock.UtcNow);
    }
}
