using Glyphite.Abstractions.Models;
using Glyphite.Host.Data;

namespace Glyphite.Tests.Unit.Support;

public static class TestData
{
    /// <summary>
    /// Appends history in the shape the application actually writes:
    /// <c>[agent_data]</c> then N turns of <c>[user_message, agent_message, turn]</c>.
    /// No synthetic leading turn marker — the real first turn follows agent_data directly,
    /// which is exactly the shape that used to be swallowed by the agent_data group.
    /// With N=3 the store has 4 groups (agent_data + 3 real turns) — enough for zone
    /// classification to find at least one to-compress group.
    /// Numbers are left to the block store, as in production.
    /// </summary>
    public static async Task AppendHistoryAsync(BlockRepository blocks, string agentId, int turns)
    {
        var list = new List<MemoryBlock> { MemoryBlock.AgentData("goal", "test") };

        for (var t = 0; t < turns; t++)
        {
            list.Add(MemoryBlock.UserMessage($"question {t}"));
            list.Add(MemoryBlock.AgentMessage($"answer {t}"));
            list.Add(MemoryBlock.TurnMarker());
        }

        await blocks.AppendBlocksAsync(agentId, list);
    }
}
