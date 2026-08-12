using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Abstractions.Interfaces;

public interface ITurnProcessor
{
    IAsyncEnumerable<TurnEvent> ProcessAsync(
        string agentId,
        string input,
        ChatOptions chatOptions,
        CancellationToken ct,
        string? agentCwd = null);

    /// <summary>Last completed iteration's usage snapshot (prompt fallback after Escape/crash).</summary>
    UsageSnapshot? LastIterationUsage { get; }
}
