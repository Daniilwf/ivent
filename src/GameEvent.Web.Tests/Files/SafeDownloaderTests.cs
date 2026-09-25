using System.Net;
using GameEvent.Infrastructure.Files;

namespace GameEvent.Web.Tests.Files;

/// <summary>
/// Downloads by link (SPEC «Трудности реализации» — скачивание аватарок по ссылке, A5, D-117): https only, hosts from
/// the list, public addresses only — checked at the connection itself — redirects checked again, size and time limits.
/// </summary>
public sealed class SafeDownloaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DownloadSettings s_settings = new();
    private static readonly FileLimits s_limits = new() { MaxUploadBytes = 1000 };

    // ---- The link ----

    [Theory]
    [InlineData("http://media.tenor.com/a.gif")]
    [InlineData("ftp://media.tenor.com/a.gif")]
    [InlineData("https://media.tenor.com:8443/a.gif")]
    [InlineData("https://user:pass@media.tenor.com/a.gif")]
    [InlineData("not a link")]
    [InlineData("/relative/a.gif")]
    [InlineData("file:///etc/passwd")]
    public void Only_https_on_the_standard_port_without_credentials(string url)
    {
        using var downloader = Downloader(_ => Png());

        Assert.Equal(SafeDownloader.UrlInvalid, downloader.Check(url)?.Code);
    }

    [Theory]
    [InlineData("https://example.com/a.gif")]
    [InlineData("https://tenor.com.evil.com/a.gif")]
    [InlineData("https://eviltenor.com/a.gif")]
    [InlineData("https://localhost/a.gif")]
    [InlineData("https://127.0.0.1/a.gif")]
    [InlineData("https://[::1]/a.gif")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public void Other_hosts_and_bare_addresses_are_refused(string url)
    {
        using var downloader = Downloader(_ => Png());

        Assert.Equal(SafeDownloader.HostNotAllowed, downloader.Check(url)?.Code);
    }

    [Theory]
    [InlineData("https://tenor.com/view/cat-123")]
    [InlineData("https://media.tenor.com/abc/cat.gif")]
    [InlineData("https://MEDIA.TENOR.COM/abc/cat.gif")]
    [InlineData("https://i.giphy.com/media/abc/giphy.gif")]
    [InlineData("https://static.klipy.com/a.gif")]
    public void The_listed_hosts_and_their_subdomains_are_allowed(string url)
    {
        using var downloader = Downloader(_ => Png());

        Assert.Null(downloader.Check(url));
    }

    // ---- The answer ----

    [Fact]
    public async Task A_picture_is_downloaded()
    {
        using var downloader = Downloader(_ => Png());

        var (content, refused) = await downloader.DownloadAsync("https://media.tenor.com/a.png", Ct);

        Assert.Null(refused);
        Assert.Equal(s_pngBytes, content);
    }

    [Fact]
    public async Task A_redirect_within_the_list_is_followed()
    {
        using var downloader = Downloader(r => r.RequestUri!.AbsolutePath == "/a" ? Redirect("/b.png") : Png());

        var (content, refused) = await downloader.DownloadAsync("https://media.tenor.com/a", Ct);

        Assert.Null(refused);
        Assert.Equal(s_pngBytes, content);
    }

    [Theory]
    [InlineData("https://example.com/b.png", SafeDownloader.HostNotAllowed)]
    [InlineData("http://media.tenor.com/b.png", SafeDownloader.UrlInvalid)]
    [InlineData("https://169.254.169.254/latest/meta-data", SafeDownloader.HostNotAllowed)]
    [InlineData("https://localhost/admin", SafeDownloader.HostNotAllowed)]
    public async Task A_redirect_is_checked_again(string location, string code)
    {
        var requested = new List<Uri>();
        using var downloader = Downloader(r =>
        {
            requested.Add(r.RequestUri!);
            return Redirect(location);
        });

        var (content, refused) = await downloader.DownloadAsync("https://media.tenor.com/a", Ct);

        Assert.Null(content);
        Assert.Equal(code, refused?.Code);
        Assert.Single(requested); // the redirect target was never asked
    }

    [Fact]
    public async Task Too_many_redirects_are_refused()
    {
        using var downloader = Downloader(r => Redirect($"/{Guid.NewGuid()}"));

        var (_, refused) = await downloader.DownloadAsync("https://media.tenor.com/a", Ct);

        Assert.Equal(SafeDownloader.DownloadFailed, refused?.Code);
    }

    [Fact]
    public async Task A_declared_size_over_the_limit_is_refused_before_reading()
    {
        using var downloader = Downloader(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2000]) };
            return response;
        });

        var (_, refused) = await downloader.DownloadAsync("https://media.tenor.com/big.gif", Ct);

        Assert.Equal(ImageProcessor.TooLarge, refused?.Code);
    }

    [Fact]
    public async Task An_undeclared_size_over_the_limit_is_cut_while_reading()
    {
        using var downloader = Downloader(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[5000])) });

        var (_, refused) = await downloader.DownloadAsync("https://media.tenor.com/big.gif", Ct);

        Assert.Equal(ImageProcessor.TooLarge, refused?.Code);
    }

    [Fact]
    public async Task An_error_answer_is_a_failed_download()
    {
        using var downloader = Downloader(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var (_, refused) = await downloader.DownloadAsync("https://media.tenor.com/missing.gif", Ct);

        Assert.Equal(SafeDownloader.DownloadFailed, refused?.Code);
    }

    [Fact]
    public async Task A_slow_answer_is_cut_by_the_time_limit()
    {
        using var downloader = new SafeDownloader(
            new DownloadSettings { TimeoutSeconds = 1 },
            s_limits,
            new FakeHandler(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return Png();
            }));

        var (_, refused) = await downloader.DownloadAsync("https://media.tenor.com/slow.gif", Ct);

        Assert.Equal(SafeDownloader.DownloadFailed, refused?.Code);
    }

    // ---- The address, at the connection itself ----

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    public async Task A_listed_host_that_resolves_to_a_private_address_is_refused(string address)
    {
        using var downloader = new SafeDownloader(new FixedResolver(IPAddress.Parse(address)), s_settings, s_limits);

        var (content, refused) = await downloader.DownloadAsync("https://media.tenor.com/a.gif", Ct);

        Assert.Null(content);
        Assert.Equal(SafeDownloader.AddressNotPublic, refused?.Code);
    }

    [Fact]
    public async Task One_private_address_among_public_ones_is_enough_to_refuse()
    {
        using var downloader = new SafeDownloader(new FixedResolver(IPAddress.Parse("151.101.1.1"), IPAddress.Parse("10.0.0.1")), s_settings, s_limits);

        var (_, refused) = await downloader.DownloadAsync("https://media.tenor.com/a.gif", Ct);

        Assert.Equal(SafeDownloader.AddressNotPublic, refused?.Code);
    }

    [Fact]
    public async Task A_host_without_addresses_is_refused()
    {
        using var downloader = new SafeDownloader(new FixedResolver(), s_settings, s_limits);

        var (_, refused) = await downloader.DownloadAsync("https://media.tenor.com/a.gif", Ct);

        Assert.Equal(SafeDownloader.AddressNotPublic, refused?.Code);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("151.101.1.1", true)]
    [InlineData("2606:4700::6810:84e5", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.255", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("::", false)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("2002:a00:1::1", false)]
    [InlineData("2001:db8::1", false)]
    public void Public_addresses_are_told_from_the_rest(string address, bool isPublic)
    {
        Assert.Equal(isPublic, PublicAddress.IsPublic(IPAddress.Parse(address)));
    }

    // ---- Helpers ----

    private static readonly byte[] s_pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private static HttpResponseMessage Png() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(s_pngBytes) };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static SafeDownloader Downloader(Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        new(s_settings, s_limits, new FakeHandler((request, _) => Task.FromResult(answer(request))));

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            answer(request, cancellationToken);
    }

    private sealed class FixedResolver(params IPAddress[] addresses) : IHostResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(addresses);
    }
}
