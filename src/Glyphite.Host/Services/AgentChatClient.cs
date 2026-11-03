using System.Runtime.CompilerServices;
using System.Text;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Glyphite.Host.Services;

/// <summary>
/// Wraps an <see cref="IChatClient"/> to:
/// <list type="bullet">
///   <item>Inject <c>user_id</c> for DeepSeek cache isolation (per-agent).</item>
///   <item>Ensure all <see cref="ChatOptions.AdditionalProperties"/> reach the actual HTTP request
///         via <see cref="JsonPatch"/>, because MEAI's <c>OpenAIChatClient</c> does not copy
///         <c>AdditionalProperties</c> into the SDK's <see cref="ChatCompletionOptions"/>.</item>
/// </list>
/// </summary>
public sealed class AgentChatClient : DelegatingChatClient
{
    private readonly string _agentId;
    private readonly string _model;

    public AgentChatClient(IChatClient inner, string agentId, string model) : base(inner)
    {
        _agentId = agentId;
        _model = model;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options,
        CancellationToken cancellationToken = default)
    {
        options = AddUserId(options);
        options = ApplyClientOptions(options);
        return await base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options = AddUserId(options);
        options = ApplyClientOptions(options);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            yield return update;
    }

    private ChatOptions AddUserId(ChatOptions? options)
    {
        // Only add user_id for DeepSeek models
        if (!_model.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase))
            return options ?? new ChatOptions();

        options ??= new ChatOptions();
        options.AdditionalProperties ??= [];
        options.AdditionalProperties["user_id"] = _agentId;
        return options;
    }

    /// <summary>
    /// The factory that turns <see cref="ChatOptions"/> into the SDK's raw
    /// <see cref="ChatCompletionOptions"/>, applying <c>AdditionalProperties</c> as a
    /// <see cref="JsonPatch"/> (MEAI never copies them into the request body).
    ///
    /// It is a named type rather than a lambda so that <see cref="ApplyClientOptions"/> can recognise
    /// its own work: a <see cref="ChatOptions"/> instance lives for the whole session and is handed to
    /// every request, so wrapping it per call stacked one more layer on every iteration — the chain
    /// grew without bound and each request re-ran all the earlier layers.
    /// </summary>
    private sealed class PatchOptionsFactory
    {
        /// <summary>The factory that existed before this wrapper, captured exactly once.</summary>
        public Func<IChatClient, object?>? Inner { get; init; }

        /// <summary>The options this wrapper belongs to — the identity used by the idempotence check.</summary>
        public ChatOptions Options { get; }

        public PatchOptionsFactory(ChatOptions options) => Options = options;

        public object? Invoke(IChatClient client)
        {
#pragma warning disable SCME0001 // JsonPatch is experimental
            var sdkOptions = Inner?.Invoke(client) as OpenAI.Chat.ChatCompletionOptions
                ?? new OpenAI.Chat.ChatCompletionOptions();

            // Read live, not captured: AdditionalProperties is mutated between requests.
            if (Options.AdditionalProperties is { } props)
            {
                foreach (var kvp in props)
                    ApplyToPatch(ref sdkOptions.Patch, kvp.Key, kvp.Value);
            }

            return sdkOptions;
#pragma warning restore SCME0001
        }
    }

    /// <summary>
    /// Converts <see cref="ChatOptions.AdditionalProperties"/> entries into
    /// <see cref="ChatCompletionOptions.Patch"/> entries so they actually
    /// reach the HTTP request body.
    /// MEAI doesn't copy <c>AdditionalProperties</c> into the SDK options,
    /// so <see cref="JsonPatch"/> is the only path to the HTTP request.
    /// Idempotent: wrapping the same options twice is a no-op.
    /// </summary>
    private static ChatOptions ApplyClientOptions(ChatOptions options)
    {
        if (options.AdditionalProperties is null || options.AdditionalProperties.Count == 0)
            return options;

        // Already ours — the wrapper reads AdditionalProperties live, so there is nothing to add.
        if (options.RawRepresentationFactory?.Target is PatchOptionsFactory own
            && ReferenceEquals(own.Options, options))
            return options;

        var factory = new PatchOptionsFactory(options) { Inner = options.RawRepresentationFactory };
        options.RawRepresentationFactory = factory.Invoke;
        return options;
    }

    /// <summary>
    /// Recursively applies a nested dictionary to a <see cref="JsonPatch"/>
    /// as a dot‑separated path.
    /// </summary>
#pragma warning disable SCME0001
    private static void ApplyToPatch(ref JsonPatch patch, string path, object? value)
    {
        if (value is Dictionary<string, object> nested)
        {
            foreach (var child in nested)
                ApplyToPatch(ref patch, $"{path}.{child.Key}", child.Value);
        }
        else if (value is not null)
        {
            var pathBytes = Encoding.UTF8.GetBytes($"$.{path}");
            patch.Set(pathBytes, value.ToString() ?? "");
        }
    }
#pragma warning restore SCME0001
}
