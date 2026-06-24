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

        var blocks = await _blockStore.LoadBlocksAsync(agentId);
        if (blocks.Count == 0)
        {
            var data = new Dictionary<string, object> { ["agent"] = _agentOpts.AgentName };
            var content = BuildAgentContent("agent", _agentOpts.AgentName);

            var agentData = MemoryBlock.Create(BlockType.agent_data, content, data: data);
            agentData.Number = 1;
            blocks.Add(agentData);
            await _blockStore.AppendBlocksAsync(agentId, blocks, 2);
        }

        // Peek blocks are cleaned by TurnProcessor before calling BuildContextAsync

        var modelStr = model ?? _defaultModel;
        var agentBlock = blocks.FirstOrDefault(b => b.Type == BlockType.agent_data);
        if (agentBlock is not null && modelStr is not null)
        {
            agentBlock.Data ??= [];
            agentBlock.Data["model"] = modelStr;

            var newContent = BuildAgentContent("agent", _agentOpts.AgentName);

            if (agentBlock.Content != newContent)
            {
                agentBlock.Content = newContent;
                await _blockStore.UpdateBlockAsync(agentId, agentBlock.Number, newContent, agentBlock.Data, modelStr);
            }
        }

        var messages = blocks
            .Where(b => b.Type != BlockType.turn)
            .Select(b => new ChatMessage(ChatRole.System, b.ToContextString())).ToList();

        return messages;
    }

    private static string BuildAgentContent(string key, string value)
    {
        return $"{key}: {value}";
    }
}
