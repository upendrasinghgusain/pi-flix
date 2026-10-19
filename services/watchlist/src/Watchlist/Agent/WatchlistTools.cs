using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAI.Chat;
using Watchlist.Data;
using Watchlist.Sonarr;
using Watchlist.Tmdb;

namespace Watchlist.Agent;

/// <summary>What one chat message did, for the app to show alongside the reply.</summary>
public sealed class TurnEffects
{
    public List<Item> Added { get; } = [];
    public string? ChoiceQuestion { get; set; }
    public List<TitleRef> Choices { get; } = [];

    /// <summary>Titles seen in search results during this turn, so choices can be shown without another lookup.</summary>
    internal Dictionary<(int, string), TitleRef> Seen { get; } = [];
}

/// <summary>
/// The agent's tools. They can search TMDb, change the watchlist and send explicit TV requests to Sonarr.
/// Every result is a small JSON string for the model to read.
/// </summary>
public sealed class WatchlistTools(WatchlistService watchlist, ItemStore items, TmdbClient tmdb, TvDownloads tvDownloads)
{
    private const int MaxResults = 8;

    public static IReadOnlyList<ChatTool> Definitions { get; } =
    [
        Tool("search_titles",
            "Search TMDb for films and TV shows by title. Use this to confirm every title before adding it.",
            """
            {"type":"object","properties":{
              "query":{"type":"string","description":"Title, or part of one"},
              "media_type":{"type":"string","enum":["movie","tv"],"description":"Only if the user said film or show"},
              "year":{"type":"integer","description":"Release year, if known"}},
             "required":["query"]}
            """),
        Tool("search_person",
            "Find an actor, director or writer by name, for descriptions like 'the new one from the Barbarian director'.",
            """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""),
        Tool("person_credits",
            "A person's films and shows, newest first, with their role (cast, Director, Writer...).",
            """
            {"type":"object","properties":{
              "person_id":{"type":"integer"},
              "role":{"type":"string","enum":["any","cast","crew"],"description":"Default any"}},
             "required":["person_id"]}
            """),
        Tool("get_title",
            "Details of one title (overview, genres, status), to check a guess.",
            IdSchema),
        Tool("where_to_watch",
            "Where a title can be watched right now, worldwide and in the UK, plus its release dates. Saves nothing.",
            IdSchema),
        Tool("add_item",
            "Add a confirmed title to the watchlist. Only use ids that came from search results.",
            IdSchema),
        Tool("download_tv",
            "Download only part of a show: one season or one episode. Use only when the user names a season or episode. Search and confirm the TV title first. This also adds the show to the watchlist, without sending the rest of it to Sonarr.",
            """
            {"type":"object","properties":{
              "tmdb_id":{"type":"integer"},
              "scope":{"type":"string","enum":["series","season","episode"]},
              "season":{"type":"integer","description":"Required for season and episode requests; zero means specials."},
              "episode":{"type":"integer","description":"Required for an episode request."}},
             "required":["tmdb_id","scope"]}
            """),
        Tool("list_items",
            "The watchlist, with each item's id and status.",
            """
            {"type":"object","properties":{
              "status":{"type":"string","enum":["waiting","available","in_library","watched","removed"],
                        "description":"Omit for everything except removed items"}}}
            """),
        Tool("remove_item",
            "Remove an item from the watchlist, by its item id from list_items. A film or show still downloading is cancelled; downloaded files are kept.",
            """{"type":"object","properties":{"item_id":{"type":"integer"}},"required":["item_id"]}"""),
        Tool("mark_watched",
            "Mark an item as watched, by its item id from list_items.",
            """{"type":"object","properties":{"item_id":{"type":"integer"}},"required":["item_id"]}"""),
        Tool("ask_user_to_choose",
            "Show the user up to 4 titles as buttons when several could be what they meant. Adds nothing.",
            """
            {"type":"object","properties":{
              "question":{"type":"string"},
              "options":{"type":"array","maxItems":4,"items":{"type":"object","properties":{
                "tmdb_id":{"type":"integer"},"media_type":{"type":"string","enum":["movie","tv"]}},
                "required":["tmdb_id","media_type"]}}},
             "required":["question","options"]}
            """),
    ];

    private const string IdSchema = """
        {"type":"object","properties":{
          "tmdb_id":{"type":"integer"},
          "media_type":{"type":"string","enum":["movie","tv"]}},
         "required":["tmdb_id","media_type"]}
        """;

    public async Task<string> ExecuteAsync(string name, string argumentsJson, string userText, TurnEffects effects, CancellationToken ct)
    {
        try
        {
            var args = JsonNode.Parse(argumentsJson) as JsonObject ?? [];
            object result = name switch
            {
                "search_titles" => await SearchTitles(args, effects, ct),
                "search_person" => await SearchPerson(args, ct),
                "person_credits" => await PersonCredits(args, effects, ct),
                "get_title" => await GetTitle(args, ct),
                "where_to_watch" => await WhereToWatch(args, ct),
                "add_item" => await AddItem(args, userText, effects, ct),
                "download_tv" => await DownloadTv(args, userText, effects, ct),
                "list_items" => await ListItems(args),
                "remove_item" => await SetStatus(args, Statuses.Removed, ct),
                "mark_watched" => await SetStatus(args, Statuses.Watched, ct),
                "ask_user_to_choose" => await AskUserToChoose(args, effects, ct),
                _ => new { error = $"unknown tool {name}" },
            };
            return JsonSerializer.Serialize(result, WatchlistService.Json);
        }
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException
                                    or FormatException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, WatchlistService.Json);
        }
    }

    private async Task<object> SearchTitles(JsonObject args, TurnEffects effects, CancellationToken ct)
    {
        var mediaType = (string?)args["media_type"];
        var hits = await tmdb.SearchTitlesAsync(
            (string?)args["query"] ?? throw new KeyNotFoundException("query is required"),
            MediaTypes.IsValid(mediaType) ? mediaType : null,
            (int?)args["year"],
            ct);
        foreach (var hit in hits)
            effects.Seen[(hit.TmdbId, hit.MediaType)] = hit;
        return hits.Take(MaxResults).Select(Summary).ToList();
    }

    private async Task<object> SearchPerson(JsonObject args, CancellationToken ct)
    {
        var people = await tmdb.SearchPeopleAsync((string?)args["name"] ?? throw new KeyNotFoundException("name is required"), ct);
        return people.Take(5).Select(p => new { person_id = p.Id, p.Name, department = p.Department, known_for = p.KnownFor }).ToList();
    }

    private async Task<object> PersonCredits(JsonObject args, TurnEffects effects, CancellationToken ct)
    {
        var role = (string?)args["role"] ?? "any";
        var credits = await tmdb.PersonCreditsAsync((int?)args["person_id"] ?? throw new KeyNotFoundException("person_id is required"), ct);
        var picked = credits
            .Where(c => role == "any" || (role == "cast") == (c.Role == "cast"))
            .Take(25)
            .ToList();
        foreach (var c in picked)
            effects.Seen[(c.Title.TmdbId, c.Title.MediaType)] = c.Title;
        return picked.Select(c => new
        {
            tmdb_id = c.Title.TmdbId,
            media_type = c.Title.MediaType,
            c.Title.Title,
            c.Title.Year,
            c.Role,
        }).ToList();
    }

    private async Task<object> GetTitle(JsonObject args, CancellationToken ct)
    {
        var (id, type) = Id(args);
        var d = await tmdb.GetTitleAsync(id, type, ct);
        return new
        {
            tmdb_id = d.Ref.TmdbId,
            media_type = d.Ref.MediaType,
            d.Ref.Title,
            d.Ref.Year,
            d.Ref.Overview,
            d.Status,
            d.Genres,
            d.Runtime,
            seasons = d.NumberOfSeasons,
        };
    }

    private async Task<object> WhereToWatch(JsonObject args, CancellationToken ct)
    {
        var (id, type) = Id(args);
        return ForModel(await watchlist.WhereToWatchAsync(id, type, ct));
    }

    private async Task<object> AddItem(JsonObject args, string userText, TurnEffects effects, CancellationToken ct)
    {
        var (id, type) = Id(args);
        var (item, outcome, availability) = await watchlist.AddAsync(id, type, userText, ct);
        if (outcome != AddOutcome.AlreadyThere)
            effects.Added.Add(item);
        return new
        {
            result = outcome switch
            {
                AddOutcome.Added => "added",
                AddOutcome.Revived => "added again (it was removed or watched before)",
                _ => "already on the list",
            },
            item_id = item.Id,
            item.Title,
            item.Year,
            item.Status,
            download = Download(item),
            availability = ForModel(availability),
        };
    }

    private async Task<object> DownloadTv(JsonObject args, string userText, TurnEffects effects, CancellationToken ct)
    {
        var tmdbId = (int?)args["tmdb_id"] ?? throw new KeyNotFoundException("tmdb_id is required");
        var scope = (string?)args["scope"] ?? throw new KeyNotFoundException("scope is required");
        var season = (int?)args["season"];
        var episode = (int?)args["episode"];
        var tvdbId = await tmdb.GetTvdbIdAsync(tmdbId, ct);
        var (item, outcome, _) = await watchlist.AddAsync(tmdbId, MediaTypes.Tv, userText, ct, send: false);
        if (outcome != AddOutcome.AlreadyThere)
            effects.Added.Add(item);
        var request = await tvDownloads.RequestAsync(tvdbId, item.Title, scope, season, episode, ct);
        await tvDownloads.TrackAsync(item, request.SeriesId);
        return new { result = request.Message, item_id = item.Id, title = item.Title, season, episode };
    }

    private static string? Download(Item item)
    {
        var arr = item.MediaType == MediaTypes.Tv ? "Sonarr" : "Radarr";
        return item.DownloadState switch
        {
            DownloadStates.Searching => $"sent to {arr}, which is looking for a copy",
            DownloadStates.Downloading => "downloading",
            DownloadStates.Imported => "already downloaded and in their library",
            DownloadStates.Pending => $"couldn't reach {arr} just now; it will be retried automatically",
            _ => null,
        };
    }

    private async Task<object> ListItems(JsonObject args)
    {
        var status = (string?)args["status"];
        var list = await items.ListAsync(status is not null && Statuses.All.Contains(status) ? status : null);
        return list.Select(i => new
        {
            item_id = i.Id,
            i.Title,
            i.Year,
            media_type = i.MediaType,
            i.Status,
            download = Download(i),
        }).ToList();
    }

    private async Task<object> SetStatus(JsonObject args, string status, CancellationToken ct)
    {
        var id = (long?)args["item_id"] ?? throw new KeyNotFoundException("item_id is required");
        var item = await watchlist.SetStatusAsync(id, status, ct);
        if (item is null)
            return new { error = $"no item {id}" };
        return new { result = status, item.Title, item.Year, download = Download(item) };
    }

    private async Task<object> AskUserToChoose(JsonObject args, TurnEffects effects, CancellationToken ct)
    {
        effects.ChoiceQuestion = (string?)args["question"] ?? "Which one?";
        effects.Choices.Clear();
        foreach (var option in (args["options"] as JsonArray ?? []).OfType<JsonObject>().Take(4))
        {
            var (id, type) = Id(option);
            effects.Choices.Add(effects.Seen.TryGetValue((id, type), out var seen)
                ? seen
                : (await tmdb.GetTitleAsync(id, type, ct)).Ref);
        }
        return new { result = "shown to the user as buttons; wait for them to pick" };
    }

    /// <summary>The parts of a check the model needs, without every country's full list.</summary>
    internal static object ForModel(Availability a) => new
    {
        available_at_home_somewhere = a.IsAvailable,
        first_home_release = a.FirstHome,
        first_cinema_release = a.FirstCinema,
        uk_home_release = a.UkHome,
        uk_cinema_release = a.UkCinema,
        uk_offers = a.Uk.Select(o => $"{o.Provider} ({o.Category})").ToList(),
        other_countries_with_offers = a.Elsewhere.Count,
        examples_elsewhere = a.Elsewhere
            .OrderBy(c => c.Country is "US" or "IE" or "CA" or "AU" ? 0 : 1)
            .Take(3)
            .Select(c => new { c.Country, offers = c.Offers.Take(4).Select(o => $"{o.Provider} ({o.Category})") })
            .ToList(),
        next_episode = a.NextEpisode,
        show_status = a.ShowStatus,
    };

    private static object Summary(TitleRef t) => new
    {
        tmdb_id = t.TmdbId,
        media_type = t.MediaType,
        t.Title,
        t.Year,
        overview = t.Overview is { Length: > 160 } o ? o[..160] + "…" : t.Overview,
    };

    private static (int Id, string Type) Id(JsonObject args)
    {
        var id = (int?)args["tmdb_id"] ?? throw new KeyNotFoundException("tmdb_id is required");
        var type = (string?)args["media_type"];
        if (!MediaTypes.IsValid(type))
            throw new KeyNotFoundException("media_type must be movie or tv");
        return (id, type!);
    }

    private static ChatTool Tool(string name, string description, string schema) =>
        ChatTool.CreateFunctionTool(name, description, BinaryData.FromString(schema));
}
