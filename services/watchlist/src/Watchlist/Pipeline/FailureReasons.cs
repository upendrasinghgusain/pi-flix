using System.Text.RegularExpressions;

namespace Watchlist.Pipeline;

/// <summary>Why an indexer failed, in words, and whether waiting is likely to fix it.</summary>
public sealed record FailureReason(string Label, bool Persistent);

/// <summary>
/// Sorts an indexer's error (from Prowlarr's log) into a kind. Timeouts, rate limits, network and
/// server errors usually pass on their own; a refused login, a Cloudflare block or a broken
/// definition don't, however long you wait.
/// </summary>
public static class FailureReasons
{
    public static FailureReason Classify(string error)
    {
        var e = error.ToLowerInvariant();
        if (e.Contains("cloudflare"))
            return new("blocked by Cloudflare", true);
        if (Code(e, "401|403") || Has(e, "unauthorized", "forbidden", "api key", "apikey", "credentials", "login"))
            return new("refused: check its login or API key", true);
        if (Has(e, "timed out", "timeout"))
            return new("timeouts", false);
        if (Code(e, "429") || Has(e, "too many requests", "rate limit", "request limit"))
            return new("rate limiting", false);
        if (Has(e, "no such host", "name or service not known", "name resolution", "connection refused", "network is unreachable"))
            return new("network errors", false);
        if (Code(e, "500|502|503|504") || Has(e, "unavailable", "server error"))
            return new("server errors", false);
        if (Has(e, "definition", "selector", "unable to parse"))
            return new("a broken indexer definition", true);
        return new("errors", false);
    }

    private static bool Has(string text, params string[] words) => words.Any(text.Contains);

    private static bool Code(string text, string codes) => Regex.IsMatch(text, $@"\b({codes})\b");
}
