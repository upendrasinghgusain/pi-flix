using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Watchlist.Access;

namespace Watchlist.Tests;

/// <summary>Signs tokens the way Cloudflare Access does, and serves the matching public keys.</summary>
public sealed class FakeAccess : HttpMessageHandler
{
    public const string Team = "testteam";
    public const string Audience = "aud-tag-123";
    public const string Email = "me@example.com";

    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "key-1" };
    public int KeyFetches { get; private set; }

    public string Token(string? email = Email, string audience = Audience, string issuer = $"https://{Team}.cloudflareaccess.com", DateTime? expires = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = email is null ? null : new Dictionary<string, object> { ["email"] = email },
            Expires = expires ?? DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        KeyFetches++;
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_key.Rsa.ExportParameters(false)) { KeyId = _key.KeyId });
        var json = JsonSerializer.Serialize(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, n = jwk.N, e = jwk.E, alg = "RS256", use = "sig" } } });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    public CloudflareAccess Guard(TimeProvider? time = null) => new(
        new HttpClient(this),
        Options.Create(new AccessOptions { TeamDomain = Team, Audience = Audience, AllowedEmail = Email }),
        time ?? TimeProvider.System,
        NullLogger<CloudflareAccess>.Instance);
}

public class CloudflareAccessTests
{
    private readonly FakeAccess _access = new();

    [Fact]
    public async Task Accepts_a_valid_token_for_the_allowed_email()
    {
        Assert.Equal(FakeAccess.Email, await _access.Guard().ValidateAsync(_access.Token(), default));
    }

    [Fact]
    public async Task Email_match_ignores_case()
    {
        Assert.NotNull(await _access.Guard().ValidateAsync(_access.Token(email: "ME@Example.com"), default));
    }

    [Theory]
    [InlineData("someone-else@example.com", FakeAccess.Audience, $"https://{FakeAccess.Team}.cloudflareaccess.com")]
    [InlineData(FakeAccess.Email, "another-app", $"https://{FakeAccess.Team}.cloudflareaccess.com")]
    [InlineData(FakeAccess.Email, FakeAccess.Audience, "https://otherteam.cloudflareaccess.com")]
    [InlineData(null, FakeAccess.Audience, $"https://{FakeAccess.Team}.cloudflareaccess.com")]
    public async Task Rejects_tokens_for_the_wrong_person_app_or_team(string? email, string audience, string issuer)
    {
        Assert.Null(await _access.Guard().ValidateAsync(_access.Token(email, audience, issuer), default));
    }

    [Fact]
    public async Task Rejects_an_expired_token()
    {
        var token = _access.Token(expires: DateTime.UtcNow.AddMinutes(-10));
        Assert.Null(await _access.Guard().ValidateAsync(token, default));
    }

    [Fact]
    public async Task Rejects_a_token_signed_by_someone_else()
    {
        var forged = new FakeAccess().Token();   // different key, same claims
        Assert.Null(await _access.Guard().ValidateAsync(forged, default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    public async Task Rejects_missing_or_garbage_tokens(string? token)
    {
        Assert.Null(await _access.Guard().ValidateAsync(token, default));
    }

    [Fact]
    public async Task Keys_are_cached_for_an_hour()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var guard = _access.Guard(time);

        await guard.ValidateAsync(_access.Token(), default);
        await guard.ValidateAsync(_access.Token(), default);
        Assert.Equal(1, _access.KeyFetches);

        time.Advance(TimeSpan.FromMinutes(61));
        await guard.ValidateAsync(_access.Token(), default);
        Assert.Equal(2, _access.KeyFetches);
    }
}

/// <summary>The whole app, run in memory in Production mode, so the lock is on.</summary>
public sealed class LockedAppTests : IClassFixture<LockedAppTests.App>
{
    private readonly App _app;

    public LockedAppTests(App app) => _app = app;

    public sealed class App : WebApplicationFactory<Program>
    {
        public FakeAccess Access { get; } = new();
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "watchlist-tests", Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Watchlist:DataDir", _dataDir);
            builder.UseSetting("Access:TeamDomain", FakeAccess.Team);
            builder.UseSetting("Access:Audience", FakeAccess.Audience);
            builder.UseSetting("Access:AllowedEmail", FakeAccess.Email);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<CloudflareAccess>();
                services.AddSingleton(sp => Access.Guard());
            });
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/items")]
    [InlineData("/api/messages")]
    public async Task Everything_is_refused_without_a_token(string path)
    {
        var response = await _app.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_open_for_the_container_check()
    {
        var response = await _app.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Requests_with_a_valid_header_get_through()
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Add(CloudflareAccess.Header, _app.Access.Token());

        var page = await client.GetAsync("/");
        var items = await client.GetAsync("/api/items");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("[]", await items.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_cookie_works_too()
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{CloudflareAccess.Cookie}={_app.Access.Token()}");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/items")).StatusCode);
    }

    [Fact]
    public async Task Someone_elses_token_is_refused()
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Add(CloudflareAccess.Header, _app.Access.Token(email: "intruder@example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/items")).StatusCode);
    }
}
