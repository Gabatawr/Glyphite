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

    /// <summary>Last iteration's cumulative hit tokens (for prompt fallback after Escape).</summary>
    long LastIterationTotalHit { get; }
    /// <summary>Last iteration's cumulative miss tokens.</summary>
    long LastIterationTotalMiss { get; }
    /// <summary>Last iteration's cumulative output tokens.</summary>
    long LastIterationTotalOutput { get; }
    /// <summary>Last iteration's per-iteration hit tokens.</summary>
    long LastIterationLastHit { get; }
    /// <summary>Last iteration's per-iteration miss tokens.</summary>
    long LastIterationLastMiss { get; }
}