using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Reveal.Sdk;
using RevealAIQuotaSample.Demo;
using RevealAIQuotaSample.Identity;
using RevealAIQuotaSample.Quota;
using Xunit;

namespace RevealAIQuotaSample.Tests;

public sealed class DevelopmentSecurityTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"quota-security-{Guid.NewGuid():n}.db");
    private readonly List<WebApplication> _apps = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.DisposeAsync();
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
        }
    }

    // ---- LocalRequest ----------------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1", "localhost:5111")]
    [InlineData("127.0.0.1", "127.0.0.1:5111")]
    [InlineData("::1", "[::1]:5111")]
    [InlineData("::1", "localhost")]
    public void Loopback_requests_to_a_local_host_name_are_local(string ip, string host)
        => Assert.True(LocalRequest.IsLocal(Http(ip, host)));

    [Theory]
    [InlineData("10.0.0.5", "localhost:5111")]       // remote client
    [InlineData("192.168.1.20", "127.0.0.1:5111")]   // remote client with a spoofed Host
    [InlineData("127.0.0.1", "demo.example.com")]    // DNS rebinding: loopback socket, foreign host name
    [InlineData("127.0.0.1", "localhost.example.com")]
    public void Remote_or_foreign_host_requests_are_not_local(string ip, string host)
        => Assert.False(LocalRequest.IsLocal(Http(ip, host)));

    [Fact]
    public void Requests_without_a_remote_address_are_not_local()
        => Assert.False(LocalRequest.IsLocal(Http(null, "localhost")));

    [Theory]
    [InlineData("X-Forwarded-For", "127.0.0.1")]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("Forwarded", "for=127.0.0.1")]
    [InlineData("X-Forwarded-Host", "localhost")]
    [InlineData("X-Forwarded-Proto", "https")]
    [InlineData("X-Real-IP", "127.0.0.1")]
    public void Forwarded_requests_are_never_local(string header, string value)
    {
        var http = Http("127.0.0.1", "localhost:5111");
        http.Request.Headers[header] = value;
        Assert.False(LocalRequest.IsLocal(http));
    }

    // ---- Development identity header -------------------------------------------------------

    [Fact]
    public void Header_identity_is_used_for_local_requests()
    {
        var http = Http("127.0.0.1", "localhost:5111");
        http.Request.Headers["X-User-Id"] = "alice";
        Assert.Equal("alice", new DevelopmentHeaderUserContextProvider().GetUserContext(http).UserId);
    }

    [Theory]
    [InlineData("10.0.0.5", "localhost:5111", null)]
    [InlineData("127.0.0.1", "evil.example", null)]
    [InlineData("127.0.0.1", "localhost:5111", "203.0.113.9")]
    public void Header_identity_is_ignored_for_non_local_requests(string ip, string host, string? forwardedFor)
    {
        var http = Http(ip, host);
        http.Request.Headers["X-User-Id"] = "alice";
        if (forwardedFor is not null)
        {
            http.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }
        Assert.Equal(DailyTokenQuotaGate.AnonymousUserId, new DevelopmentHeaderUserContextProvider().GetUserContext(http).UserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("anonymous")]
    [InlineData("<script>")]
    [InlineData("a b")]
    public void Invalid_header_values_are_anonymous(string value)
    {
        var http = Http("127.0.0.1", "localhost");
        http.Request.Headers["X-User-Id"] = value;
        Assert.Equal(DailyTokenQuotaGate.AnonymousUserId, new DevelopmentHeaderUserContextProvider().GetUserContext(http).UserId);
    }

    [Fact]
    public void Authenticated_provider_ignores_the_header()
    {
        var http = Http("127.0.0.1", "localhost");
        http.Request.Headers["X-User-Id"] = "alice";
        Assert.Equal(DailyTokenQuotaGate.AnonymousUserId, new AuthenticatedUserContextProvider().GetUserContext(http).UserId);
    }

    // ---- Endpoint mapping ------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/dev/quota")]
    [InlineData("GET", "/dev/quota/alice/calls")]
    [InlineData("POST", "/dev/quota/alice/reset")]
    public async Task Development_endpoints_do_not_exist_in_production(string method, string path)
    {
        var server = await StartAsync(Environments.Production, enableDevEndpoints: true);
        var response = await SendAsync(server, method, path, "127.0.0.1", "localhost", demoHeader: true);
        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Development_endpoints_are_off_unless_enabled()
    {
        var server = await StartAsync(Environments.Development, enableDevEndpoints: false);
        var response = await SendAsync(server, "GET", "/dev/quota", "127.0.0.1", "localhost");
        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Development_endpoints_answer_local_requests()
    {
        var server = await StartAsync(Environments.Development, enableDevEndpoints: true);
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(server, "GET", "/dev/quota", "127.0.0.1", "localhost")).StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await SendAsync(server, "POST", "/dev/quota/alice/reset", "127.0.0.1", "localhost", demoHeader: true)).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/dev/quota", "10.0.0.5", "localhost", null)]
    [InlineData("POST", "/dev/quota/alice/reset", "10.0.0.5", "localhost", null)]
    [InlineData("POST", "/dev/quota/alice/reset", "127.0.0.1", "evil.example", null)]
    [InlineData("POST", "/dev/quota/alice/reset", "127.0.0.1", "localhost", "203.0.113.9")]
    [InlineData("GET", "/dev/quota/alice/calls", "127.0.0.1", "localhost", "127.0.0.1")]
    public async Task Development_endpoints_reject_remote_or_forwarded_requests(
        string method, string path, string ip, string host, string? forwardedFor)
    {
        var server = await StartAsync(Environments.Development, enableDevEndpoints: true);
        var response = await SendAsync(server, method, path, ip, host, demoHeader: true, forwardedFor: forwardedFor);
        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reset_requires_the_demo_request_header()
    {
        var server = await StartAsync(Environments.Development, enableDevEndpoints: true);
        var response = await SendAsync(server, "POST", "/dev/quota/alice/reset", "127.0.0.1", "localhost", demoHeader: false);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/quota/me")]
    [InlineData("/quota/me/requests")]
    public async Task Quota_me_is_unauthorized_without_an_identity(string path)
    {
        var server = await StartAsync(Environments.Production, enableDevEndpoints: false, useHeaderIdentity: false);
        var response = await SendAsync(server, "GET", path, "127.0.0.1", "localhost", userId: "alice");
        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/demo/")]
    [InlineData("/demo/app.js")]
    public async Task Demo_page_is_hidden_from_remote_clients(string path)
    {
        var server = await StartAsync(Environments.Development, enableDevEndpoints: true, demoPage: true);
        var response = await SendAsync(server, "GET", path, "10.0.0.5", "localhost");
        Assert.Equal(StatusCodes.Status404NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Root_redirects_local_clients_to_the_demo_page()
    {
        var server = await StartAsync(Environments.Development, enableDevEndpoints: true, demoPage: true);
        var response = await SendAsync(server, "GET", "/", "127.0.0.1", "localhost");
        Assert.Equal(StatusCodes.Status302Found, response.StatusCode);
        Assert.Equal("/demo/", response.Headers.Location.ToString());
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static DefaultHttpContext Http(string? ip, string host)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = ip is null ? null : IPAddress.Parse(ip);
        http.Request.Host = HostString.FromUriComponent(host);
        return http;
    }

    private async Task<TestServer> StartAsync(string environment, bool enableDevEndpoints, bool useHeaderIdentity = true, bool demoPage = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.Configure<QuotaOptions>(o =>
        {
            o.EnableDevelopmentEndpoints = enableDevEndpoints;
            o.DatabasePath = _dbPath;
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<QuotaStore>();
        builder.Services.AddSingleton<IRVUserContextProvider>(useHeaderIdentity
            ? new DevelopmentHeaderUserContextProvider()
            : new AuthenticatedUserContextProvider());

        var app = builder.Build();
        if (demoPage)
        {
            app.UseDemoPage();
        }
        app.MapQuotaEndpoints();
        await app.StartAsync();
        _apps.Add(app);
        return app.GetTestServer();
    }

    private static async Task<HttpResponse> SendAsync(
        TestServer server, string method, string path, string ip, string host,
        bool demoHeader = false, string? forwardedFor = null, string? userId = null)
        => (await server.SendAsync(http =>
        {
            http.Request.Method = method;
            http.Request.Path = path;
            http.Request.Host = HostString.FromUriComponent(host);
            http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            if (demoHeader) http.Request.Headers[LocalRequest.DemoRequestHeader] = "1";
            if (forwardedFor is not null) http.Request.Headers["X-Forwarded-For"] = forwardedFor;
            if (userId is not null) http.Request.Headers["X-User-Id"] = userId;
        })).Response;
}
