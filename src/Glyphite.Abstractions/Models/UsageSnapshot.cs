namespace Glyphite.Abstractions.Models;

/// <summary>
/// Immutable snapshot of token usage for a turn — or for the last completed
/// iteration of a turn (used as the prompt fallback after Escape/crash).
/// </summary>
public readonly record struct UsageSnapshot(
    long TotalHit,
    long TotalMiss,
    long TotalOutput,
    long LastHit = 0,
    long LastMiss = 0);
