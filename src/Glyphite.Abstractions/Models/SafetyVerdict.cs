namespace Glyphite.Abstractions.Models;

/// <summary>
/// Result of a safety check evaluation for a shell command.
/// </summary>
public record SafetyVerdict(bool Allow, string Why);
