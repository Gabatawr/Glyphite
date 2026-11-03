namespace Glyphite.Abstractions.Models;

/// <summary>
/// Result of a safety check evaluation for a shell command.
/// </summary>
/// <param name="Allow">True only when the check actually concluded the command is safe.</param>
/// <param name="Why">Reason shown to the model/user when the command is not allowed.</param>
/// <param name="Inconclusive">
/// The check could not reach a verdict at all — the model call failed, returned an empty answer, or
/// answered in a shape that could not be parsed. <see cref="Allow"/> is false in that case, and the
/// decision must fall back to a human: a broken checker is not an approval.
/// </param>
public record SafetyVerdict(bool Allow, string Why, bool Inconclusive = false)
{
    /// <summary>The checker itself failed; a human has to decide.</summary>
    public static SafetyVerdict Unavailable(string why) => new(false, why, Inconclusive: true);
}
