namespace Watchlist;

/// <summary>General settings, from the "Watchlist" config section.</summary>
public sealed class WatchlistOptions
{
    /// <summary>Folder holding watchlist.db.</summary>
    public string DataDir { get; set; } = "App_Data";

    /// <summary>The agent stops calling OpenAI after this many chat messages in a day.</summary>
    public int DailyMessageLimit { get; set; } = 200;

    /// <summary>Decides what "today" means for release dates and the daily limit.</summary>
    public string TimeZone { get; set; } = "Europe/London";
}

public sealed class TmdbOptions
{
    /// <summary>TMDb "API Read Access Token" (v4), sent as a bearer token.</summary>
    public string ReadToken { get; set; } = "";
}

public sealed class OpenAIOptions
{
    public string ApiKey { get; set; } = "";
    public string ChatModel { get; set; } = "";
    public string TranscribeModel { get; set; } = "";
}

/// <summary>Cloudflare Access, which sits in front of the whole hostname.</summary>
public sealed class AccessOptions
{
    /// <summary>Team name, e.g. "myteam" for myteam.cloudflareaccess.com.</summary>
    public string TeamDomain { get; set; } = "";

    /// <summary>The Access application's "Application Audience (AUD) Tag".</summary>
    public string Audience { get; set; } = "";

    /// <summary>The only email allowed in.</summary>
    public string AllowedEmail { get; set; } = "";

    /// <summary>Skip the check entirely. Honoured only in the Development environment.</summary>
    public bool Disabled { get; set; }

    public string TeamHost => TeamDomain.Contains('.') ? TeamDomain : $"{TeamDomain}.cloudflareaccess.com";
}
