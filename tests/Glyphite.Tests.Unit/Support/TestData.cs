using Glyphite.Abstractions.Models;
using Glyphite.Host.Data;

namespace Glyphite.Tests.Unit.Support;

public static class TestData
{
    /// <summary>
    /// Appends a compaction-friendly history: [agent_data, turn] then N turns of
    /// [user_message, agent_message, turn]. With N=3 the store has 4 turn groups
    /// (agent_data group + 3 real turns) — enough for zone classification to find
    /// at least one to-compress group.
    /// </summary>
    public static async Task AppendHistoryAsync(BlockRepository blocks, string agentId, int turns)
    {
        var agentData = MemoryBlock.AgentData("goal", "test");
        agentData.Number = 1;
        var leadingTurn = MemoryBlock.TurnMarker();
        leadingTurn.Number = 2;
        var list = new List<MemoryBlock> { agentData, leadingTurn };

        var num = 3.0;
        for (var t = 0; t < turns; t++)
        {
            var question = MemoryBlock.UserMessage($"question {t}");
            question.Number = num++;
            var answer = MemoryBlock.AgentMessage($"answer {t}");
            answer.Number = num++;
            var turn = MemoryBlock.TurnMarker();
            turn.Number = num++;
            list.Add(question);
            list.Add(answer);
            list.Add(turn);
        }
        await blocks.AppendBlocksAsync(agentId, list, num);
    }
}
