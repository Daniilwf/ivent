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
    /// <summary>
    /// Hosts a link may point to (SPEC: Tenor, Giphy, Klipy): the media hosts themselves, not every subdomain — a
    /// forgotten marketing subdomain taken over would serve anything. <c>*.name</c> allows the subdomains of a name.
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } =
    [
        "media.tenor.com", "c.tenor.com", "media1.tenor.com",
        "i.giphy.com", "media.giphy.com", "media0.giphy.com", "media1.giphy.com", "media2.giphy.com", "media3.giphy.com", "media4.giphy.com",
        "*.klipy.com",
    ];

    /// <summary>Downloads at once on the whole site: each holds up to a file in memory.</summary>
    public int MaxConcurrent { get; init; } = 2;

    /// <summary>How long a download waits for a free place before the answer «busy».</summary>
    public int QueueWaitSeconds { get; init; } = 2;

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
            // Global unicast 2000::/3 only (no loopback, IPv4-compatible, NAT64, discard, unique or link local, multicast),
            // and not the special ranges inside it
            var b = address.GetAddressBytes();
            return (b[0] & 0xE0) == 0x20
                && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) // documentation 2001:db8::/32
                && !(b[0] == 0x20 && b[1] == 0x02) // 6to4 2002::/16 — may embed a private IPv4
                && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) // Teredo 2001::/32
                && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x02 && b[4] == 0x00 && b[5] == 0x00) // benchmarking 2001:2::/48
                && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && (b[3] & 0xF0) is 0x10 or 0x20) // ORCHID 2001:10::/28, 2001:20::/28
                && !(b[0] == 0x3F && (b[1] & 0xF0) == 0xF0); // documentation 3fff::/20
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
    public const string Busy = "file.busy";

    private readonly DownloadSettings _settings;
    private readonly FileLimits _limits;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _slots;

    public SafeDownloader(IHostResolver resolver, DownloadSettings settings, FileLimits limits)
        : this(settings, limits, Handler(resolver ?? throw new ArgumentNullException(nameof(resolver)), settings))
    {
    }

    /// <summary>With a handler of the tests' own: the link, redirect and size rules without a network.</summary>
    internal SafeDownloader(DownloadSettings settings, FileLimits limits, HttpMessageHandler handler)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _slots = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrent));
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("GameEvent/1.0 (avatar download)");
    }

    public async Task<(byte[]? Content, Rejection? Rejection)> DownloadAsync(string url, CancellationToken ct)
    {
        if (Check(url) is { } refused)
        {
            return (null, refused);
        }

        if (!await _slots.WaitAsync(TimeSpan.FromSeconds(_settings.QueueWaitSeconds), ct))
        {
            return (null, new Rejection(Busy, "The server is busy with other downloads."));
        }

        try
        {
            return await DownloadCheckedAsync(url, ct);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<(byte[]? Content, Rejection? Rejection)> DownloadCheckedAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));

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
                using var buffer = new MemoryStream((int)Math.Min(response.Content.Headers.ContentLength ?? 256 * 1024, _limits.MaxUploadBytes));
                var chunk = new byte[64 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
                {
                    // A GIF is kept as it is: past its own limit there is no point reading on
                    var limit = IsGif(buffer, chunk, read) ? _limits.MaxGifBytes : _limits.MaxUploadBytes;
                    if (buffer.Length + read > limit)
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
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UriFormatException && !ct.IsCancellationRequested)
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
        return _settings.AllowedHosts.Any(allowed => allowed.StartsWith("*.", StringComparison.Ordinal)
                ? host.EndsWith(allowed[1..], StringComparison.Ordinal)
                : host == allowed)
            ? null
            : new Rejection(HostNotAllowed, $"Links to {host} are not accepted.");
    }

    public void Dispose()
    {
        _client.Dispose();
        _slots.Dispose();
    }

    private static bool IsGif(MemoryStream buffer, byte[] chunk, int read)
    {
        ReadOnlySpan<byte> head = buffer.Length >= 4 ? buffer.GetBuffer().AsSpan(0, 4) : chunk.AsSpan(0, Math.Min(read, 4));
        return head.StartsWith("GIF8"u8);
    }

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
