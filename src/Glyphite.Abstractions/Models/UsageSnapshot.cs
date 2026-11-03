namespace Glyphite.Abstractions.Models;

/// <summary>
/// Immutable snapshot of token usage for a turn — or for the last completed
/// iteration of a turn (used as the prompt fallback after Escape/crash).
///
/// <see cref="TotalOutput"/> is billed and already includes <see cref="TotalReasoning"/>.
/// For the size of the context only <see cref="TotalHit"/> + <see cref="TotalMiss"/> is meaningful.
/// </summary>
public readonly record struct UsageSnapshot(
    long TotalHit,
    long TotalMiss,
    long TotalOutput,
    long LastHit = 0,
    long LastMiss = 0,
    long TotalReasoning = 0);