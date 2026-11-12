using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using GameEvent.Web.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The whole route table at once (security audit 2026-11, D-461): every API endpoint is either on the short list of
/// anonymous ones or answers 401 to the anonymous, and every admin or test endpoint answers 403 to a player and a
/// spectator. A new endpoint that forgets its policy fails here, not in production.
/// </summary>
public sealed partial class EndpointAuthorizationTests : IAsyncLifetime
{
    // What anyone may call without signing in: the token for the sign-in form, the sign-in itself, signing out and
    // the site's status (the maintenance banner, the version)
    private static readonly HashSet<string> s_anonymous =
    [
        "GET /api/auth/antiforgery",
        "POST /api/auth/login",
        "POST /api/auth/logout",
        "GET /api/status",
    ];

    // Mapped in the Test environment only (A7): a request that fails on purpose, for the error journal's tests
    private const string TestOnlyFailure = "GET /api/test/fail";

    private readonly SiteFactory _site = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _site.SeedAsync();

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public void The_route_table_has_the_endpoints_this_test_walks()
    {
        var endpoints = ApiEndpoints();

        // A sanity floor: an empty sweep would pass everything below
        Assert.True(endpoints.Count > 60, $"Only {endpoints.Count} API endpoints found.");
        Assert.Contains(endpoints, e => e.Key == "POST /api/admin/accounts");
        Assert.Contains(endpoints, e => e.Key == "POST /api/seasons/{seasonId:guid}/roll");
        Assert.Contains(endpoints, e => e.Key == "GET /api/test/clock");
    }

    [Fact]
    public void Only_the_listed_endpoints_are_open_to_the_anonymous()
    {
        var open = ApiEndpoints().Where(e => !e.RequiresSignIn && e.Key != TestOnlyFailure).Select(e => e.Key).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(s_anonymous.Order(StringComparer.Ordinal), open);
    }

    [Fact]
    public void Every_admin_and_test_path_requires_the_admin_policy()
    {
        var loose = ApiEndpoints()
            .Where(e => e.Route.StartsWith("/api/admin", StringComparison.Ordinal) || e.Route.StartsWith("/api/test", StringComparison.Ordinal))
            .Where(e => !e.AdminOnly && e.Key != TestOnlyFailure)
            .Select(e => e.Key)
            .ToList();

        Assert.Empty(loose);
    }

    [Fact]
    public async Task The_anonymous_get_401_from_every_endpoint_that_needs_a_sign_in()
    {
        var client = await _site.AnonymousAsync();
        var wrong = new List<string>();
        foreach (var endpoint in ApiEndpoints().Where(e => e.RequiresSignIn))
        {
            var status = await SendAsync(client, endpoint);
            if (status != HttpStatusCode.Unauthorized)
            {
                wrong.Add($"{endpoint.Key}: {(int)status}");
            }
        }

        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData("masha")] // a player outside the season
    [InlineData("vasya")] // a player of the season
    [InlineData("zritel")] // a spectator
    public async Task Players_and_spectators_get_403_from_every_admin_endpoint(string login)
    {
        var client = await _site.SignedInAsync(login);
        var wrong = new List<string>();
        foreach (var endpoint in ApiEndpoints().Where(e => e.AdminOnly))
        {
            var status = await SendAsync(client, endpoint);
            if (status != HttpStatusCode.Forbidden)
            {
                wrong.Add($"{endpoint.Key}: {(int)status}");
            }
        }

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task Spectators_get_403_from_every_game_action()
    {
        var client = await _site.SignedInAsync("zritel");
        var wrong = new List<string>();
        foreach (var endpoint in ApiEndpoints().Where(e => e.PlayerOnly))
        {
            var status = await SendAsync(client, endpoint);
            if (status != HttpStatusCode.Forbidden)
            {
                wrong.Add($"{endpoint.Key}: {(int)status}");
            }
        }

        Assert.Contains(ApiEndpoints(), e => e.PlayerOnly);
        Assert.Empty(wrong);
    }

    private sealed record ApiEndpoint(string Method, string Route, bool RequiresSignIn, bool AdminOnly, bool PlayerOnly, bool Multipart)
    {
        public string Key => $"{Method} {Route}";
    }

    private List<ApiEndpoint> ApiEndpoints()
    {
        var sources = _site.Services.GetServices<EndpointDataSource>();
        var list = new List<ApiEndpoint>();
        foreach (var endpoint in sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var route = "/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('/');
            if (!route.StartsWith("/api/", StringComparison.Ordinal))
            {
                continue;
            }

            var metadata = endpoint.Metadata;
            var authorize = metadata.GetOrderedMetadata<IAuthorizeData>();
            var anonymous = metadata.GetMetadata<IAllowAnonymous>() is not null;
            var requiresSignIn = authorize.Count > 0 && !anonymous;
            var policies = authorize.Select(a => a.Policy).ToList();
            foreach (var method in metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                list.Add(new ApiEndpoint(
                    method,
                    route.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : route,
                    requiresSignIn,
                    requiresSignIn && policies.Contains(Policies.Admin),
                    requiresSignIn && policies.Contains(Policies.Player),
                    metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IAcceptsMetadata>()?.ContentTypes.Contains("multipart/form-data") == true));
            }
        }

        return list;
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, ApiEndpoint endpoint)
    {
        var url = Parameter().Replace(endpoint.Route, m => m.Groups["name"].Value switch
        {
            "seasonId" => SiteFactory.SeasonId.ToString(),
            _ when m.Groups["constraint"].Value.Contains("guid", StringComparison.Ordinal) => Guid.NewGuid().ToString(),
            // A value the route accepts, or routing answers 404 before anyone is asked who is calling
            _ when m.Groups["constraint"].Value.Contains("int", StringComparison.Ordinal) => "1",
            _ => "x",
        });
        using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), url);
        // A body of the type the endpoint takes: otherwise routing answers 415 before anyone is asked who is calling
        if (endpoint.Multipart)
        {
            request.Content = new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "commandId" } };
        }
        else if (endpoint.Method is not "GET" and not "HEAD")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    [GeneratedRegex(@"\{(?<name>[^}:?*]+)(?<constraint>[^}]*)\}")]
    private static partial Regex Parameter();
}
