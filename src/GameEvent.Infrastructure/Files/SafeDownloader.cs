using System.Net;
using System.Net.Sockets;
using GameEvent.Engine.Kernel;

namespace GameEvent.Infrastructure.Files;

/// <summary>Where a host name leads; the real one asks DNS, tests give fixed answers.</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>Downloads by link (D-117), from the <c>Files:Download</c> section of the configuration.</summary>
public sealed record DownloadSettings
{
    /// <summary>Hosts a link may point to — each with its subdomains (SPEC: Tenor, Giphy, Klipy).</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = ["tenor.com", "giphy.com", "klipy.com"];

    public int TimeoutSeconds { get; init; } = 15;

    public int MaxRedirects { get; init; } = 3;
}

/// <summary>Public internet addresses only: no loopback, private, link-local, shared, reserved or multicast ranges (D-117).</summary>
public static class PublicAddress
{
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 // "this" network
                || b[0] == 10 // private
                || b[0] == 127 // loopback
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // shared (CGNAT)
                || (b[0] == 169 && b[1] == 254) // link-local, cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) // private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0) // IETF protocol assignments
                || (b[0] == 192 && b[1] == 0 && b[2] == 2) // documentation
                || (b[0] == 192 && b[1] == 88 && b[2] == 99) // 6to4 relay
                || (b[0] == 192 && b[1] == 168) // private
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100) // documentation
                || (b[0] == 203 && b[1] == 0 && b[2] == 113) // documentation
                || b[0] >= 224); // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.IPv6None)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC // unique local fc00::/7
                || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) // NAT64 64:ff9b::/96 — may lead to a private IPv4
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) // documentation 2001:db8::/32
                || (b[0] == 0x20 && b[1] == 0x02) // 6to4 2002::/16 — may embed a private IPv4
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)); // Teredo 2001::/32
        }

        return false;
    }
}

/// <summary>
/// Downloads a picture by link without letting the link steer the server (SPEC «Трудности реализации» — скачивание
/// аватарок по ссылке, D-117): https on the standard port only, hosts from the allowed list, the address resolved here and
/// checked to be public, and the connection made to that very address (no second DNS answer can point it inward);
/// redirects followed by hand, each checked again; size and time limits. What comes back is processed like an upload.
/// </summary>
public sealed class SafeDownloader : IDisposable
{
    public const string UrlInvalid = "file.urlInvalid";
    public const string HostNotAllowed = "file.hostNotAllowed";
    public const string AddressNotPublic = "file.addressNotPublic";
    public const string DownloadFailed = "file.downloadFailed";

    private readonly DownloadSettings _settings;
    private readonly FileLimits _limits;
    private readonly HttpClient _client;

    public SafeDownloader(IHostResolver resolver, DownloadSettings settings, FileLimits limits)
        : this(settings, limits, Handler(resolver ?? throw new ArgumentNullException(nameof(resolver)), settings))
    {
    }

    /// <summary>With a handler of the tests' own: the link, redirect and size rules without a network.</summary>
    internal SafeDownloader(DownloadSettings settings, FileLimits limits, HttpMessageHandler handler)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("GameEvent/1.0 (avatar download)");
    }

    public async Task<(byte[]? Content, Rejection? Rejection)> DownloadAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        if (Check(url) is { } refused)
        {
            return (null, refused);
        }

        var current = new Uri(url);
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (redirects >= _settings.MaxRedirects)
                    {
                        return (null, new Rejection(DownloadFailed, "Too many redirects."));
                    }

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (Check(next.OriginalString) is { } redirectRefused)
                    {
                        return (null, redirectRefused);
                    }

                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return (null, new Rejection(DownloadFailed, $"The link answered {(int)response.StatusCode}."));
                }

                if (response.Content.Headers.ContentLength > _limits.MaxUploadBytes)
                {
                    return (null, TooLarge());
                }

                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                var chunk = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
                {
                    if (buffer.Length + read > _limits.MaxUploadBytes)
                    {
                        return (null, TooLarge());
                    }

                    buffer.Write(chunk, 0, read);
                }

                return (buffer.ToArray(), null);
            }
        }
        catch (HttpRequestException e) when (e.InnerException is AddressRefusedException)
        {
            return (null, new Rejection(AddressNotPublic, "The link leads to an address that is not public."));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return (null, new Rejection(DownloadFailed, "The picture could not be downloaded."));
        }
    }

    /// <summary>A link the server may follow: https, the standard port, no credentials, a host name from the list.</summary>
    internal Rejection? Check(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0 || url.Length > 2000)
        {
            return new Rejection(UrlInvalid, "Only https links are accepted.");
        }

        if (uri.HostNameType != UriHostNameType.Dns)
        {
            return new Rejection(HostNotAllowed, "Links by address are not accepted.");
        }

        var host = uri.IdnHost.ToLowerInvariant().TrimEnd('.');
        return _settings.AllowedHosts.Any(allowed => host == allowed || host.EndsWith("." + allowed, StringComparison.Ordinal))
            ? null
            : new Rejection(HostNotAllowed, $"Links to {host} are not accepted.");
    }

    public void Dispose() => _client.Dispose();

    private Rejection TooLarge() => new(ImageProcessor.TooLarge, $"A file is at most {_limits.MaxUploadBytes / 1024 / 1024} MB.");

    // The connection goes to the address checked here: whatever DNS answers later cannot turn it inward
    private static SocketsHttpHandler Handler(IHostResolver resolver, DownloadSettings settings) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await resolver.ResolveAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || !addresses.All(PublicAddress.IsPublic))
            {
                throw new AddressRefusedException();
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private sealed class AddressRefusedException() : IOException("The host resolves to an address that is not public.");
}
