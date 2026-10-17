using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI.Audio;
using Watchlist;
using Watchlist.Access;
using Watchlist.Agent;
using Watchlist.Alerts;
using Watchlist.Data;
using Watchlist.Push;
using Watchlist.Tmdb;

var builder = WebApplication.CreateBuilder(args);

// Personal settings (your services, quiet hours) live next to the database, editable on the Pi.
var dataDir = builder.Configuration["Watchlist:DataDir"] ?? new WatchlistOptions().DataDir;
builder.Configuration.AddJsonFile(Path.GetFullPath(Path.Combine(dataDir, "settings.json")), optional: true, reloadOnChange: true);

builder.Services.Configure<WatchlistOptions>(builder.Configuration.GetSection("Watchlist"));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection("Alerts"));
builder.Services.Configure<PushOptions>(builder.Configuration.GetSection("Push"));
builder.Services.Configure<TmdbOptions>(builder.Configuration.GetSection("Tmdb"));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection("OpenAI"));
builder.Services.Configure<AccessOptions>(builder.Configuration.GetSection("Access"));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
{
    var dir = sp.GetRequiredService<IOptions<WatchlistOptions>>().Value.DataDir;
    Directory.CreateDirectory(dir);
    var db = new Database(Path.Combine(dir, "watchlist.db"));
    db.Migrate();
    return db;
});
builder.Services.AddSingleton<ItemStore>();
builder.Services.AddSingleton<MessageStore>();
builder.Services.AddSingleton<AlertStore>();
builder.Services.AddSingleton<PushStore>();
builder.Services.AddSingleton<MetaStore>();
builder.Services.AddSingleton(sp => VapidKeys.LoadOrCreateAsync(sp.GetRequiredService<MetaStore>()).GetAwaiter().GetResult());
builder.Services.AddHttpClient<IPushSender, WebPushSender>();
builder.Services.AddTransient<CheckRunner>();
builder.Services.AddHostedService<ReleaseChecker>();
builder.Services.AddHttpClient<TmdbClient>();
builder.Services.AddTransient<WatchlistService>();
builder.Services.AddTransient<WatchlistTools>();
builder.Services.AddSingleton<IChatModel, OpenAIChatModel>();
builder.Services.AddTransient<ChatAgent>();
// A singleton, so the Access signing keys are fetched once an hour rather than per request.
builder.Services.AddHttpClient(nameof(CloudflareAccess));
builder.Services.AddSingleton(sp => new CloudflareAccess(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(CloudflareAccess)),
        sp.GetRequiredService<IOptions<AccessOptions>>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<CloudflareAccess>>()));

var app = builder.Build();

// Fail at start-up rather than run unlocked by accident.
var access = app.Services.GetRequiredService<IOptions<AccessOptions>>().Value;
var accessDisabled = access.Disabled && app.Environment.IsDevelopment();
if (!accessDisabled && (access.TeamDomain == "" || access.Audience == "" || access.AllowedEmail == ""))
    throw new InvalidOperationException("Access:TeamDomain, Access:Audience and Access:AllowedEmail must be set.");
_ = app.Services.GetRequiredService<Database>();

app.MapGet("/health", () => Results.Text("ok"));

app.Use(async (context, next) =>
{
    if (accessDisabled || context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }
    var token = context.Request.Headers[CloudflareAccess.Header].FirstOrDefault()
                ?? context.Request.Cookies[CloudflareAccess.Cookie];
    var guard = context.RequestServices.GetRequiredService<CloudflareAccess>();
    if (await guard.ValidateAsync(token, context.RequestAborted) is null)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapPost("/chat", async (ChatRequest request, ChatAgent agent, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
        return Results.BadRequest();
    var result = await agent.HandleAsync(request.Text.Trim(), ct);
    return Results.Ok(new
    {
        reply = result.Reply,
        added = result.Effects.Added.Select(ItemDto.From),
        choice = result.Effects.Choices.Count == 0
            ? null
            : new { question = result.Effects.ChoiceQuestion, options = result.Effects.Choices },
    });
});

// A tapped choice button adds directly, without another trip through the model.
api.MapPost("/choose", async (ChooseRequest request, WatchlistService watchlist, MessageStore messages, CancellationToken ct) =>
{
    if (!MediaTypes.IsValid(request.MediaType))
        return Results.BadRequest();
    var (item, outcome, _) = await watchlist.AddAsync(request.TmdbId, request.MediaType, request.AddedText, ct);
    var reply = outcome == AddOutcome.AlreadyThere
        ? $"{Describe(item)} is already on your list."
        : $"Added {Describe(item)}.";
    await messages.AddAsync(MessageStore.User, $"(chose {Describe(item)})");
    await messages.AddAsync(MessageStore.Assistant, reply);
    return Results.Ok(new { reply, added = outcome == AddOutcome.AlreadyThere ? [] : new[] { ItemDto.From(item) } });
});

api.MapPost("/transcribe", async (IFormFile audio, IOptions<OpenAIOptions> openAI, CancellationToken ct) =>
{
    var o = openAI.Value;
    if (o.ApiKey == "" || o.TranscribeModel == "")
        return Results.Problem("Voice isn't set up yet: add the OpenAI key and transcription model to the settings.");
    var client = new AudioClient(o.TranscribeModel, o.ApiKey);
    await using var stream = audio.OpenReadStream();
    var name = string.IsNullOrEmpty(audio.FileName) ? "voice.webm" : audio.FileName;
    var transcription = await client.TranscribeAudioAsync(stream, name, new AudioTranscriptionOptions { Language = "en" }, ct);
    return Results.Ok(new { text = transcription.Value.Text.Trim() });
}).DisableAntiforgery();

api.MapGet("/messages", async (MessageStore messages) =>
    (await messages.RecentAsync(50)).Select(m => new { m.Role, m.Text, m.CreatedAt }));

api.MapGet("/items", async (string? status, ItemStore items) =>
    status is not null && !Statuses.All.Contains(status)
        ? Results.BadRequest()
        : Results.Ok((await items.ListAsync(status)).Select(ItemDto.From)));

api.MapPatch("/items/{id:long}", async (long id, ItemPatch patch, ItemStore items) =>
{
    if (await items.GetAsync(id) is null)
        return Results.NotFound();
    if (patch.Status is not null)
    {
        if (!Statuses.All.Contains(patch.Status))
            return Results.BadRequest();
        await items.SetStatusAsync(id, patch.Status);
    }
    if (patch.Notes is not null)
        await items.SetNotesAsync(id, patch.Notes.Trim() == "" ? null : patch.Notes.Trim());
    if (patch.ClearSnooze)
        await items.SetSnoozeAsync(id, null);
    else if (patch.SnoozedUntil is not null)
        await items.SetSnoozeAsync(id, patch.SnoozedUntil);
    return Results.Ok(ItemDto.From((await items.GetAsync(id))!));
});

api.MapPut("/items/{id:long}/match", async (long id, ChooseRequest request, WatchlistService watchlist, ItemStore items, CancellationToken ct) =>
{
    if (!MediaTypes.IsValid(request.MediaType))
        return Results.BadRequest();
    if (await items.GetAsync(id) is null)
        return Results.NotFound();
    var item = await watchlist.ReplaceMatchAsync(id, request.TmdbId, request.MediaType, ct);
    return item is null
        ? Results.Conflict(new { error = "That title is already on your list." })
        : Results.Ok(ItemDto.From(item));
});

api.MapPost("/items/{id:long}/check", async (long id, WatchlistService watchlist, CancellationToken ct) =>
    await watchlist.CheckAsync(id, ct) is { } result
        ? Results.Ok(new { item = ItemDto.From(result.Item), alerts = result.Alerts.Select(a => a.Text) })
        : Results.NotFound());

api.MapGet("/alerts", async (AlertStore alerts) =>
    (await alerts.RecentAsync(50)).Select(a => new { a.ItemId, a.Kind, a.Text, a.CreatedAt, a.SentAt }));

api.MapGet("/push/key", (VapidKeys keys) => Results.Ok(new { publicKey = keys.PublicKey }));

api.MapPost("/push/subscribe", async (PushSubscriptionRequest request, PushStore pushes) =>
{
    if (request.Keys is null || !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https")
        return Results.BadRequest();
    await pushes.SaveAsync(request.Endpoint, request.Keys.P256dh, request.Keys.Auth);
    return Results.Ok();
});

api.MapPost("/push/unsubscribe", async (PushSubscriptionRequest request, PushStore pushes) =>
{
    await pushes.DeleteAsync(request.Endpoint);
    return Results.Ok();
});

api.MapPost("/push/test", async (PushStore pushes, IPushSender sender, CancellationToken ct) =>
{
    var devices = await pushes.AllAsync();
    var results = new List<PushResult>();
    foreach (var device in devices)
        results.Add(await sender.SendAsync(device, new { title = "Watchlist", body = "Alerts are on.", tag = "test", url = "./#list" }, ct));
    return Results.Ok(new { devices = devices.Count, sent = results.Count(r => r == PushResult.Sent) });
});

api.MapGet("/search", async (string q, TmdbClient tmdb, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(q) ? Results.BadRequest() : Results.Ok((await tmdb.SearchTitlesAsync(q, ct: ct)).Take(10)));

app.Run();

static string Describe(Item item) => item.Year is null ? item.Title : $"{item.Title} ({item.Year})";

public sealed record ChatRequest(string Text);

public sealed record PushKeys(string P256dh, string Auth);

public sealed record PushSubscriptionRequest(string Endpoint, PushKeys? Keys);

public sealed record ChooseRequest(int TmdbId, string MediaType, string? AddedText = null);

public sealed record ItemPatch(string? Status, string? Notes, DateOnly? SnoozedUntil, bool ClearSnooze = false);

public sealed record ItemDto(
    long Id, int TmdbId, string MediaType, string Title, int? Year, string? PosterPath, string? Overview,
    string Status, DateOnly? SnoozedUntil, string? Notes, string? AddedText, DateTimeOffset AddedAt,
    DateTimeOffset? LastCheckedAt, JsonElement? Availability)
{
    public static ItemDto From(Item i) => new(
        i.Id, i.TmdbId, i.MediaType, i.Title, i.Year, i.PosterPath, i.Overview, i.Status, i.SnoozedUntil,
        i.Notes, i.AddedText, i.AddedAt, i.LastCheckedAt,
        i.AvailabilityJson is null ? null : JsonDocument.Parse(i.AvailabilityJson).RootElement.Clone());
}

public partial class Program;
