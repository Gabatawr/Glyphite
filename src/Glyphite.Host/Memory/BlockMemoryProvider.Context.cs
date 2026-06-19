using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Memory;

public partial class BlockMemoryProvider
{
    public async Task<List<ChatMessage>> BuildContextAsync(string sessionId, string? model = null, int? contextWindow = null)
    {
        await _store.EnsureSessionAsync(sessionId);
        model ??= await _store.GetAgentModelAsync(sessionId);

        var blocks = await _store.LoadBlocksAsync(sessionId);
        if (blocks.Count == 0)
        {
            var data = new Dictionary<string, object> { ["agent"] = _agentOpts.AgentName };
            var content = BuildAgentContent("agent", _agentOpts.AgentName, null);

            if (AgentFilePath is not null && File.Exists(AgentFilePath))
            {
                data["agent_file"] = await File.ReadAllTextAsync(AgentFilePath);
                data["agent_file_path"] = AgentFilePath;
            }

            var agentData = MemoryBlock.Create(BlockType.agent_data, content, data: data);
            agentData.Number = 1;
            blocks.Add(agentData);
            await _store.AppendBlocksAsync(sessionId, blocks, 2);
        }

        // Clean up peek blocks from previous turn
        var peekCount = await _store.RemovePeekBlocksAsync(sessionId);
        if (peekCount > 0)
            blocks = await _store.LoadBlocksAsync(sessionId);

        var modelStr = model ?? _defaultModel;
        var agentBlock = blocks.FirstOrDefault(b => b.Type == BlockType.agent_data);
        if (agentBlock is not null && modelStr is not null)
        {
            agentBlock.Data ??= [];
            agentBlock.Data["model"] = modelStr;

            var newContent = BuildAgentContent("agent", _agentOpts.AgentName, agentBlock.Data);

            if (_memOpts.ReloadAgentFile && AgentFilePath is not null)
            {
                if (File.Exists(AgentFilePath))
                {
                    var fileTime = File.GetLastWriteTimeUtc(AgentFilePath);
                    if (agentBlock.UpdatedAt is null || fileTime > agentBlock.UpdatedAt.Value)
                    {
                        agentBlock.Data["agent_file"] = await File.ReadAllTextAsync(AgentFilePath);
                        agentBlock.Data["agent_file_path"] = AgentFilePath;
                    }
                }
                else if (agentBlock.Data.ContainsKey("agent_file"))
                {
                    agentBlock.Data.Remove("agent_file");
                    agentBlock.Data.Remove("agent_file_path");
                }

                newContent = BuildAgentContent("agent", _agentOpts.AgentName, agentBlock.Data);
            }

            if (agentBlock.Content != newContent)
            {
                agentBlock.Content = newContent;
                await _store.UpdateBlockAsync(sessionId, agentBlock.Number, newContent, agentBlock.Data, modelStr);
            }
        }

        var messages = blocks.Select(b =>
            new ChatMessage(ChatRole.System, b.ToContextString())).ToList();

        if (_encoding is not null && messages.Count > 0)
        {
            var totalTokens = 0;
            foreach (var msg in messages)
                totalTokens += _encoding.Encode(msg.Text ?? "").Count;

            if (_compOpts is not null && contextWindow.HasValue && totalTokens >= (int)(_compOpts.AutoThreshold / 100.0 * contextWindow.Value))
            {
                if (_compOpts.AutoCompress)
                {
                    var protectedSet = new HashSet<string>(_memOpts.ProtectedBlockTypes, StringComparer.OrdinalIgnoreCase);
                    var deletable = blocks.Any(b => !protectedSet.Contains(b.Type.ToString()));
                    if (deletable)
                    {
                        var protectedTypes = string.Join(", ", _memOpts.ProtectedBlockTypes);
                        messages.Add(new ChatMessage(ChatRole.System,
                            $"AUTO-COMPRESSION: Context is at {totalTokens} tokens — clean old blocks now. Use `list` to inspect memory, then `clean` old tool calls, tool results, and reasoning blocks. PROTECTED (keep): {protectedTypes}."));
                    }
                }
            }
        }

        return messages;
    }

    private static string BuildAgentContent(string key, string value, Dictionary<string, object>? data)
    {
        var content = $"{key}: {value}";
        if (data?.ContainsKey("agent_file") == true)
            content += "; AgentFile: AGENTS.md";
        return content;
    }
}
