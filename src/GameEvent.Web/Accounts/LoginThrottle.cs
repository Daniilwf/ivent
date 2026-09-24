using GameEvent.Engine.Kernel;

namespace GameEvent.Web.Accounts;

/// <summary>
/// Limits failed sign-ins (D-26, D-67). The hard limit is per login and client address: a guesser is stopped,
/// but cannot lock the owner out from elsewhere. A much higher per-login ceiling slows a guess spread over
/// many addresses. Windows are temporary; nobody can lock an account for good.
/// An attempt is reserved before the password is checked, so parallel requests cannot slip past the limit.
/// </summary>
public sealed class LoginThrottle(IClock clock, IConfiguration configuration)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);

    private readonly int _perAddress = configuration.GetValue("Security:LoginFailuresPerAddress", 5);
    private readonly TimeSpan _addressWindow = TimeSpan.FromMinutes(configuration.GetValue("Security:LoginFailureWindowMinutes", 15));
    private readonly int _perLogin = configuration.GetValue("Security:LoginFailuresPerLogin", 50);
    private readonly TimeSpan _loginWindow = TimeSpan.FromMinutes(60);

    /// <summary>Reserves an attempt; false means the caller is throttled and must not check the password.</summary>
    public bool TryBegin(string normalizedLogin, string clientKey)
    {
        lock (_gate)
        {
            var pair = Pair(normalizedLogin, clientKey);
            if (Count(pair, _addressWindow) >= _perAddress || Count(normalizedLogin, _loginWindow) >= _perLogin)
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
