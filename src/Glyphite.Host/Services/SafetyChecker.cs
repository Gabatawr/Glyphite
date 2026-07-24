using System.Text.Json;
using System.Text.RegularExpressions;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Services;

/// <summary>
/// Evaluates shell commands using the same LLM that's driving the agent.
/// Reads recent conversation context, makes a direct LLM call,
/// records usage, and returns a structured verdict.
/// Cache is preserved because the same <see cref="IChatClient"/> and model are used.
/// </summary>
internal sealed partial class SafetyChecker(
    IChatClient chatClient,
    IBlockStore blockStore,
    IAgentStore agentStore) : ISafetyChecker
{
    public async Task<SafetyVerdict> CheckAsync(string agentId, string command, CancellationToken ct)
    {
        // 1. Load blocks from the current turn (from last turn marker or agent_data)
        var context = await LoadContextAsync(agentId);

        // 2. Construct the safety evaluation prompt
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, """
                You are a safety checker integrated into an AI coding agent.
                A shell command was requested. Evaluate whether it is safe to execute
                given the current conversation context.

                Consider:
                - Is this a destructive command (rm, dd, wipe, etc.)?
                - Is the target a system path or a project-local path?
                - Could this command cause data loss or system instability?
                - Is this command appropriate for the current task?
                - Is there a safer alternative?

                Respond with raw JSON only — no markdown, no code fences.
                Use this exact format:
                {"allow":true,"why":""}
                or
                {"allow":false,"why":"brief reason in the user's language"}
                """),
            new(ChatRole.User, $"CONTEXT:\n{context}\n\nCOMMAND:\n{command}\n\nEvaluate safety.")
        };

        // 3. Make LLM call (same model as the main agent — cache preserved)
        ChatResponse? response;
        try
        {
            response = await chatClient.GetResponseAsync(messages, new ChatOptions
            {
                MaxOutputTokens = 512,
            }, ct);
        }
        catch (Exception ex)
        {
            // If LLM call fails, default to safe (allow) to not block the user
            return new SafetyVerdict(true, $"Safety check unavailable: {ex.Message}");
        }

        // 4. Record usage on the agent's session
        try
        {
            if (response.RawRepresentation is not null)
            {
                using var doc = UsageParser.Normalize(response.RawRepresentation);
                if (doc is not null)
                {
                    var (hit, miss, output) = UsageParser.Parse(doc);
                    var model = await agentStore.GetAgentModelAsync(agentId);
                    if (hit > 0 || miss > 0 || output > 0)
                        await agentStore.RecordUsageAsync(agentId, hit, miss, output, model: model);
                }
            }
        }
        catch
        {
            // Best-effort: don't let usage recording failure block execution
        }

        // 5. Parse the structured JSON response
        var text = response.Messages
            .LastOrDefault(m => m.Role == ChatRole.Assistant)
            ?.Text?.Trim();

        if (string.IsNullOrEmpty(text))
            return new SafetyVerdict(true, "Safety check returned empty response — proceeding.");

        return ParseVerdict(text);
    }

    /// <summary>
    /// Loads blocks from the last <see cref="BlockType.turn"/> marker (or <see cref="BlockType.agent_data"/>
    /// if no turn marker exists — first turn) to the end. This gives the safety checker the full
    /// current conversation turn as context.
    /// </summary>
    private async Task<string> LoadContextAsync(string agentId)
    {
        try
        {
            var blocks = await blockStore.LoadBlocksAsync(agentId);
            if (blocks.Count == 0)
                return "(no conversation context)";

            // Find the last turn marker — everything after it is the current turn
            var lastTurnIdx = blocks.FindLastIndex(b => b.Type == BlockType.turn);

            // If we have a turn marker, start from the block after it
            // If no turn marker yet (first turn), start from the last user_message
            // If no user_message (subagent), start from the last agent_task
            // Fallback: entire session
            var startIdx = lastTurnIdx >= 0
                ? lastTurnIdx + 1
                : blocks.FindLastIndex(b => b.Type == BlockType.user_message);
            if (startIdx < 0)
                startIdx = blocks.FindLastIndex(b => b.Type == BlockType.agent_task);
            if (startIdx < 0) startIdx = 0;

            var currentTurn = blocks.Skip(startIdx);

            return string.Join("", currentTurn.Select(b => b.ToContextString()));
        }
        catch
        {
            return "(context unavailable)";
        }
    }

    /// <summary>
    /// Parses the LLM response text, extracting JSON from possible markdown code fences.
    /// Falls back to safe (allow) on malformed responses.
    /// </summary>
    private static SafetyVerdict ParseVerdict(string text)
    {
        try
        {
            // Extract JSON from markdown code fences if present
            var jsonMatch = JsonBlockRegex().Match(text);
            var json = jsonMatch.Success ? jsonMatch.Groups[1].Value : text;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var allow = root.TryGetProperty("allow", out var allowProp) && allowProp.ValueKind == JsonValueKind.True;
            var why = root.TryGetProperty("why", out var whyProp) ? whyProp.GetString() ?? "" : "";

            return new SafetyVerdict(allow, why);
        }
        catch (JsonException)
        {
            // Malformed JSON — safe fallback: allow but note the parsing issue
            return new SafetyVerdict(true, "Could not parse safety verdict — proceeding with caution.");
        }
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.Multiline)]
    private static partial Regex JsonBlockRegex();
}
