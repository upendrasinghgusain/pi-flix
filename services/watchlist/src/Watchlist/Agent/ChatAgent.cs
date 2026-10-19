using Microsoft.Extensions.Options;
using OpenAI.Chat;
using Watchlist.Data;

namespace Watchlist.Agent;

/// <summary>The model call, behind an interface so tests can script the replies.</summary>
public interface IChatModel
{
    bool IsConfigured { get; }
    Task<ChatCompletion> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatCompletionOptions options, CancellationToken ct);
}

public sealed class OpenAIChatModel(IOptions<OpenAIOptions> options) : IChatModel
{
    private readonly ChatClient? _client =
        string.IsNullOrEmpty(options.Value.ApiKey) || string.IsNullOrEmpty(options.Value.ChatModel)
            ? null
            : new ChatClient(options.Value.ChatModel, options.Value.ApiKey);

    public bool IsConfigured => _client is not null;

    public async Task<ChatCompletion> CompleteAsync(IReadOnlyList<ChatMessage> messages, ChatCompletionOptions options, CancellationToken ct) =>
        await (_client ?? throw new InvalidOperationException("OpenAI isn't configured")).CompleteChatAsync(messages, options, ct);
}

public sealed record ChatResult(string Reply, TurnEffects Effects);

/// <summary>
/// One chat message in, one reply out. The model picks tools, the app runs them and hands back
/// the results, and this repeats until the model answers or the tool budget runs out.
/// </summary>
public sealed class ChatAgent(
    IChatModel model,
    WatchlistTools tools,
    MessageStore messages,
    WatchlistService watchlist,
    IOptions<WatchlistOptions> options,
    ILogger<ChatAgent> log)
{
    public const int MaxToolCalls = 6;
    private const int HistoryMessages = 8;

    /// <param name="conversationId">One per opening of the app; the agent only sees this conversation's history.</param>
    public async Task<ChatResult> HandleAsync(string text, string conversationId, CancellationToken ct)
    {
        var effects = new TurnEffects();
        if (!model.IsConfigured)
            return new ChatResult("The assistant isn't set up yet: add the OpenAI key and model to the settings.", effects);

        if (await messages.CountUserMessagesSinceAsync(watchlist.StartOfToday) >= options.Value.DailyMessageLimit)
            return new ChatResult($"That's {options.Value.DailyMessageLimit} messages today, the daily limit. Use the list for now; the chat is back tomorrow.", effects);

        var conversation = new List<ChatMessage> { new SystemChatMessage(SystemPrompt(watchlist.Today)) };
        foreach (var m in await messages.RecentAsync(conversationId, HistoryMessages))
            conversation.Add(m.Role == MessageStore.User ? new UserChatMessage(m.Text) : new AssistantChatMessage(m.Text));
        conversation.Add(new UserChatMessage(text));
        await messages.AddAsync(conversationId, MessageStore.User, text);

        var chatOptions = new ChatCompletionOptions();
        foreach (var tool in WatchlistTools.Definitions)
            chatOptions.Tools.Add(tool);

        var reply = "";
        var calls = 0;
        while (true)
        {
            if (calls >= MaxToolCalls)
                chatOptions.ToolChoice = ChatToolChoice.CreateNoneChoice();

            var completion = await model.CompleteAsync(conversation, chatOptions, ct);
            if (completion.FinishReason != ChatFinishReason.ToolCalls || completion.ToolCalls.Count == 0)
            {
                reply = string.Concat(completion.Content.Select(p => p.Text)).Trim();
                break;
            }

            conversation.Add(new AssistantChatMessage(completion));
            foreach (var call in completion.ToolCalls)
            {
                calls++;
                var result = await tools.ExecuteAsync(call.FunctionName, call.FunctionArguments.ToString(), text, effects, ct);
                log.LogInformation("Tool {Tool}({Args}) -> {Length} chars", call.FunctionName, call.FunctionArguments, result.Length);
                conversation.Add(new ToolChatMessage(call.Id, result));
            }
        }

        if (reply.Length == 0)
            reply = effects.Choices.Count > 0 ? effects.ChoiceQuestion ?? "Which one?" : "Done.";
        await messages.AddAsync(conversationId, MessageStore.Assistant, reply);
        return new ChatResult(reply, effects);
    }

    internal static string SystemPrompt(DateOnly today) => $"""
        You look after one person's watchlist of films and TV shows. They live in the UK. Today is {today:dddd d MMMM yyyy}.

        Work out what they want: add something to track, explicitly request a film or TV download, remove it, mark it watched, see the list, or ask where something can be watched or when it's out.
        They may also just chat; only change the list when they ask.
        - Tracking: "add ...", "I want to watch ...", or "put ... on my list" means add it to the watchlist.
        - Downloading a film: an explicit request such as "download ..." adds it to the watchlist and Radarr.
        - Downloading TV: when the user explicitly asks to download or queue a show, season or episode, search_titles must first confirm the TV title, then call download_tv with scope "series" (the whole show), "season" or "episode". For season/episode requests, get the exact season and episode numbers from the user text; ask if either is missing or unclear. download_tv also adds the show to the watchlist, so do not call add_item first. It reports that Sonarr is searching; this does not guarantee a matching release exists.
        - Asking "where can I watch ...", "is ... out yet", "what's ... about" saves nothing.
        - Films on the list are downloaded automatically and appear in their library. add_item says how that's going; mention it briefly, e.g. "Radarr is looking for a copy".

        Finding the right title:
        - They often describe a title vaguely. You may use your own knowledge to guess it, but confirm every guess with search_titles, or search_person and person_credits for descriptions involving people. Never add a title that hasn't come back from a search.
        - If one result clearly fits, add it.
        - If several could fit (remakes, same name, a film and a show), call ask_user_to_choose with up to 4 options, and add nothing.
        - If nothing fits, say so and ask for a detail, such as the year or an actor.
        - To remove an item or mark it watched, find its item_id with list_items first. If it's unclear which one they mean, ask.
        - "No, the other one" refers to your previous action: undo it (remove_item) and add the right one.

        Replies:
        - Plain text, one or two short sentences. No markdown, no ids.
        - Name the title with its year, e.g. "Weapons (2025)".
        - About availability, lead with the first home release anywhere in the world (country and how: digital, disc, streaming), then what's on in the UK. Say if it's only in cinemas so far. Use country names, not codes.
        """;
}
