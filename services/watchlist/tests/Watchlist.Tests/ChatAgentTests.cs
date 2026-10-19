using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Watchlist.Agent;
using Watchlist.Data;
using Watchlist.Sonarr;
using Watchlist.Tmdb;

namespace Watchlist.Tests;

/// <summary>Plays back scripted model replies and remembers what the agent sent each time.</summary>
public sealed class ScriptedModel(params ChatCompletion[] replies) : IChatModel
{
    private readonly Queue<ChatCompletion> _replies = new(replies);
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];
    public List<ChatCompletionOptions> Options { get; } = [];
    public bool IsConfigured => true;

    public Task<ChatCompletion> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatCompletionOptions options, CancellationToken ct)
    {
        Calls.Add(messages.ToList());
        Options.Add(options);
        return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : Say("(out of script)"));
    }

    public static ChatCompletion Say(string text) =>
        OpenAIChatModelFactory.ChatCompletion(
            id: Guid.NewGuid().ToString(),
            finishReason: ChatFinishReason.Stop,
            content: new ChatMessageContent(text),
            role: ChatMessageRole.Assistant);

    public static ChatCompletion Call(params (string Name, string Args)[] calls) =>
        OpenAIChatModelFactory.ChatCompletion(
            id: Guid.NewGuid().ToString(),
            finishReason: ChatFinishReason.ToolCalls,
            toolCalls: calls.Select((c, i) => ChatToolCall.CreateFunctionToolCall($"call_{i}_{Guid.NewGuid():N}", c.Name, BinaryData.FromString(c.Args))),
            role: ChatMessageRole.Assistant);
}

public sealed class ChatAgentTests : IDisposable
{
    private readonly TestWorld _world = new();

    public void Dispose() => _world.Dispose();

    private ChatAgent Agent(IChatModel model, int dailyLimit = 200) => new(
        model,
        new WatchlistTools(_world.Watchlist, _world.Items, _world.Tmdb.Client(), UnconfiguredSonarr()),
        _world.Messages,
        _world.Watchlist,
        Options.Create(new WatchlistOptions { DailyMessageLimit = dailyLimit }),
        NullLogger<ChatAgent>.Instance);

    private static TvDownloads UnconfiguredSonarr() => new(new SonarrClient(new HttpClient(),
        Options.Create(new SonarrOptions())));

    [Fact]
    public async Task Searches_then_adds_and_reports_what_was_added()
    {
        _world.Tmdb.On("search/multi", Samples.WeaponsSearch).On("movie/1078605", Samples.WeaponsMovie);
        var model = new ScriptedModel(
            ScriptedModel.Call(("search_titles", """{"query":"Weapons"}""")),
            ScriptedModel.Call(("add_item", """{"tmdb_id":1078605,"media_type":"movie"}""")),
            ScriptedModel.Say("Added Weapons (2025). It's out digitally in the United States."));

        var result = await Agent(model).HandleAsync("that new zach cregger horror", "c1", default);

        Assert.Equal("Added Weapons (2025). It's out digitally in the United States.", result.Reply);
        var added = Assert.Single(result.Effects.Added);
        Assert.Equal("Weapons", added.Title);
        Assert.Equal("that new zach cregger horror", added.AddedText);

        // The search result reached the model, without the person in it.
        var toolResult = model.Calls[1].OfType<ToolChatMessage>().Single().Content[0].Text;
        Assert.Contains("1078605", toolResult);
        Assert.DoesNotContain("Someone", toolResult);
    }

    [Fact]
    public async Task Choices_are_returned_as_buttons_and_nothing_is_added()
    {
        _world.Tmdb.On("search/multi", Samples.WeaponsSearch);
        var model = new ScriptedModel(
            ScriptedModel.Call(("search_titles", """{"query":"Weapons"}""")),
            ScriptedModel.Call(("ask_user_to_choose", """
                {"question":"Which Weapons?","options":[
                  {"tmdb_id":1078605,"media_type":"movie"},{"tmdb_id":999,"media_type":"movie"}]}
                """)),
            ScriptedModel.Say("Which Weapons?"));

        var result = await Agent(model).HandleAsync("weapons", "c1", default);

        Assert.Empty(result.Effects.Added);
        Assert.Equal("Which Weapons?", result.Effects.ChoiceQuestion);
        Assert.Equal([2025, 1999], result.Effects.Choices.Select(c => c.Year));
        Assert.Empty(await _world.Items.ListAsync());
    }

    [Fact]
    public async Task Stops_offering_tools_after_the_budget_is_spent()
    {
        _world.Tmdb.On("search/multi", """{"results": []}""");
        var search = ("search_titles", """{"query":"x"}""");
        var model = new ScriptedModel(
            ScriptedModel.Call(search, search, search),
            ScriptedModel.Call(search, search, search),
            ScriptedModel.Say("I couldn't find it."));

        var result = await Agent(model).HandleAsync("something obscure", "c1", default);

        Assert.Equal("I couldn't find it.", result.Reply);
        Assert.Equal(3, model.Calls.Count);
        Assert.Equal(ChatAgent.MaxToolCalls, _world.Tmdb.Requests.Count);
        Assert.NotNull(model.Options[2].ToolChoice);   // forced to answer without tools
    }

    [Fact]
    public async Task Tool_errors_go_back_to_the_model_instead_of_failing_the_message()
    {
        var model = new ScriptedModel(
            ScriptedModel.Call(("add_item", """{"tmdb_id":42,"media_type":"movie"}""")),   // TMDb returns 404
            ScriptedModel.Say("Sorry, I couldn't find that one."));

        var result = await Agent(model).HandleAsync("add 42", "c1", default);

        Assert.Equal("Sorry, I couldn't find that one.", result.Reply);
        Assert.Contains("error", model.Calls[1].OfType<ToolChatMessage>().Single().Content[0].Text);
    }

    [Fact]
    public async Task Remove_by_item_id()
    {
        var (item, _) = await _world.Items.AddAsync(new TitleRef(7, MediaTypes.Movie, "Dune", 2021, null, null), null);
        var model = new ScriptedModel(
            ScriptedModel.Call(("list_items", "{}")),
            ScriptedModel.Call(("remove_item", $$"""{"item_id":{{item.Id}}}""")),
            ScriptedModel.Say("Removed Dune (2021)."));

        await Agent(model).HandleAsync("remove dune", "c1", default);

        Assert.Equal(Statuses.Removed, (await _world.Items.GetAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task History_is_kept_and_sent_with_the_next_message()
    {
        await Agent(new ScriptedModel(ScriptedModel.Say("Added Dune (2021)."))).HandleAsync("add dune", "c1", default);
        var model = new ScriptedModel(ScriptedModel.Say("Swapped it."));

        await Agent(model).HandleAsync("no, the other one", "c1", default);

        var sent = model.Calls[0];
        Assert.IsType<SystemChatMessage>(sent[0]);
        Assert.Equal("add dune", ((UserChatMessage)sent[1]).Content[0].Text);
        Assert.Equal("Added Dune (2021).", ((AssistantChatMessage)sent[2]).Content[0].Text);
        Assert.Equal("no, the other one", ((UserChatMessage)sent[3]).Content[0].Text);
        Assert.Equal(4, (await _world.Messages.RecentAsync("c1", 10)).Count);
    }

    [Fact]
    public async Task A_new_conversation_starts_clean()
    {
        await Agent(new ScriptedModel(ScriptedModel.Say("Added Dune (2021)."))).HandleAsync("add dune", "yesterday", default);
        var model = new ScriptedModel(ScriptedModel.Say("Hello."));

        await Agent(model).HandleAsync("hi", "today", default);

        var sent = model.Calls[0];
        Assert.Equal(2, sent.Count);   // the system prompt and "hi", nothing from the other conversation
        Assert.Equal("hi", ((UserChatMessage)sent[1]).Content[0].Text);
    }

    [Fact]
    public async Task Adding_a_film_reports_that_radarr_is_looking()
    {
        using var world = new TestWorld(radarr: true);
        world.Tmdb.On("movie/1078605", Samples.WeaponsMovie);
        var model = new ScriptedModel(
            ScriptedModel.Call(("add_item", """{"tmdb_id":1078605,"media_type":"movie"}""")),
            ScriptedModel.Say("Added Weapons (2025); Radarr is looking for a copy."));
        var agent = new ChatAgent(model, new WatchlistTools(world.Watchlist, world.Items, world.Tmdb.Client(), UnconfiguredSonarr()), world.Messages,
            world.Watchlist, Options.Create(new WatchlistOptions()), NullLogger<ChatAgent>.Instance);

        await agent.HandleAsync("I want to watch Weapons", "c1", default);

        var toolResult = model.Calls[1].OfType<ToolChatMessage>().Single().Content[0].Text;
        Assert.Contains("looking for a copy", toolResult);
    }

    [Fact]
    public async Task Daily_limit_stops_calling_the_model()
    {
        await _world.Messages.AddAsync("old", MessageStore.User, "one");
        await _world.Messages.AddAsync("old", MessageStore.User, "two");
        var model = new ScriptedModel();

        var result = await Agent(model, dailyLimit: 2).HandleAsync("three", "c1", default);

        Assert.Contains("daily limit", result.Reply);
        Assert.Empty(model.Calls);
    }
}
