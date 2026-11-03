using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Memory;

public partial class BlockMemoryProvider
{
    public async Task<List<ChatMessage>> BuildContextAsync(string agentId, string? model = null, int? contextWindow = null)
    {
        await _agentStore.EnsureSessionAsync(agentId);
        model ??= await _agentStore.GetAgentModelAsync(agentId);

        // Read per call instead of from a DI snapshot: `Agent:AgentName` is a file value, and the point of
        // the config service is that an edit lands on the next turn rather than on the next `/new`.
        var agentName = (await _config.GetOptionsAsync<AgentOptions>(AgentOptions.Section, agentId)).AgentName;

        var blocks = await _blockStore.LoadBlocksAsync(agentId);
        if (blocks.Count == 0)
        {
            var data = new Dictionary<string, object> { ["agent"] = agentName };
            var content = BuildAgentContent("agent", agentName);

            var agentData = MemoryBlock.Create(BlockType.agent_data, content, data: data);
            blocks.Add(agentData);
            await _blockStore.AppendBlocksAsync(agentId, blocks);
        }

        // Peek blocks are cleaned by TurnProcessor before calling BuildContextAsync

        var modelStr = model ?? _defaultModel;
        var agentBlock = blocks.FirstOrDefault(b => b.Type == BlockType.agent_data);
        if (agentBlock is not null && modelStr is not null)
        {
            agentBlock.Data ??= [];
            agentBlock.Data["model"] = modelStr;

            var newContent = BuildAgentContent("agent", agentName);

            if (agentBlock.Content != newContent)
            {
                agentBlock.Content = newContent;
                await _blockStore.UpdateBlockAsync(agentId, agentBlock.Number, newContent, agentBlock.Data, modelStr);
            }
        }

        var messages = blocks
            .Where(b => b.Type != BlockType.turn && b.Type != BlockType.auto_tool)
            .Select(b => new ChatMessage(ChatRole.System, b.ToContextString())).ToList();

        return messages;
    }

    private static string BuildAgentContent(string key, string value)
    {
        return $"{key}: {value}";
    }
}
