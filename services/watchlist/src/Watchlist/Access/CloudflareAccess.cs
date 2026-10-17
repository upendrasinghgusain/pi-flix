using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Watchlist.Access;

/// <summary>
/// The second lock. Cloudflare Access signs a token for every request it lets through
/// (Cf-Access-Jwt-Assertion header, or the CF_Authorization cookie). Each request must carry
/// a valid one for this application and the allowed email, so the app still refuses strangers
/// if Access is ever switched off by mistake.
/// </summary>
public sealed class CloudflareAccess(HttpClient http, IOptions<AccessOptions> options, TimeProvider time, ILogger<CloudflareAccess> log)
{
    public const string Header = "Cf-Access-Jwt-Assertion";
    public const string Cookie = "CF_Authorization";

    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinRefreshGap = TimeSpan.FromMinutes(5);

    private readonly AccessOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private IList<SecurityKey> _keys = [];
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    /// <summary>The signed-in email if the token is valid and allowed, otherwise null.</summary>
    public async Task<string?> ValidateAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var result = await ValidateWithKeysAsync(token, forceRefresh: false, ct);
        if (result is { IsValid: false, Exception: SecurityTokenSignatureKeyNotFoundException })
            result = await ValidateWithKeysAsync(token, forceRefresh: true, ct);   // Cloudflare rotated its keys

        if (!result.IsValid)
        {
            log.LogWarning("Rejected Access token: {Reason}", result.Exception?.Message);
            return null;
        }

        var email = result.Claims.TryGetValue("email", out var e) ? e as string : null;
        if (!string.Equals(email, _options.AllowedEmail, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Rejected Access token for a different email");
            return null;
        }
        return email;
    }

    private async Task<TokenValidationResult> ValidateWithKeysAsync(string token, bool forceRefresh, CancellationToken ct)
    {
        var keys = await GetKeysAsync(forceRefresh, ct);
        return await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = $"https://{_options.TeamHost}",
            ValidAudience = _options.Audience,
            IssuerSigningKeys = keys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        });
    }

    private async Task<IList<SecurityKey>> GetKeysAsync(bool forceRefresh, CancellationToken ct)
    {
        var age = time.GetUtcNow() - _fetchedAt;
        if (age < KeyLifetime && !(forceRefresh && age > MinRefreshGap))
            return _keys;

        await _refresh.WaitAsync(ct);
        try
        {
            age = time.GetUtcNow() - _fetchedAt;
            if (age < KeyLifetime && !(forceRefresh && age > MinRefreshGap))
                return _keys;

            var json = await http.GetStringAsync($"https://{_options.TeamHost}/cdn-cgi/access/certs", ct);
            _keys = new JsonWebKeySet(json).GetSigningKeys();
            _fetchedAt = time.GetUtcNow();
            return _keys;
        }
        finally
        {
            _refresh.Release();
        }
    }
}
