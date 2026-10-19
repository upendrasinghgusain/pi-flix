using System.Security.Cryptography;
using System.Text;

namespace Watchlist.Access;

/// <summary>
/// The lock on /hooks, which Radarr and Sonarr call from the Pi's *arr network. It needs the shared
/// secret (webhook user name "radarr", for both, and password), and anything that came in through
/// Cloudflare is refused, so the hook can't be reached from the internet even with the secret.
/// </summary>
public static class Hooks
{
    public const string User = "radarr";

    public static bool Allowed(HttpRequest request, string secret)
    {
        if (secret == "")
            return false;   // webhook off
        if (request.Headers.ContainsKey("Cf-Ray") || request.Headers.ContainsKey("Cf-Connecting-IP"))
            return false;

        var header = request.Headers.Authorization.FirstOrDefault();
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;
        byte[] given;
        try
        {
            given = Convert.FromBase64String(header["Basic ".Length..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes($"{User}:{secret}"));
    }
}
