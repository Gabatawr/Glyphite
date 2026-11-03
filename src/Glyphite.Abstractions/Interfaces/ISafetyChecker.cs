using Glyphite.Abstractions.Models;

namespace Glyphite.Abstractions.Interfaces;

/// <summary>
/// Evaluates whether a shell command is safe to execute given recent conversation context.
/// Used by the interactive confirmation "Check" option.
/// </summary>
public interface ISafetyChecker
{
    /// <summary>
    /// Evaluates the command and returns a verdict.
    /// If <see cref="SafetyVerdict.Allow"/> is false, execution is blocked
    /// and <see cref="SafetyVerdict.Why"/> explains the reason.
    /// When the check itself fails to produce a verdict, <see cref="SafetyVerdict.Inconclusive"/> is
    /// true (and <see cref="SafetyVerdict.Allow"/> false) — the caller must ask the human instead.
    /// </summary>
    Task<SafetyVerdict> CheckAsync(string agentId, string command, CancellationToken ct = default);
}
