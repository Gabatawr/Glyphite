using Glyphite.Abstractions.Interfaces;
using static Glyphite.Host.Tools.InteractiveConfirmation;

namespace Glyphite.Host.Tools;

/// <summary>
/// The one seam between "the agent wants to run something that needs approval" and "a human decides".
///
/// It exists because the decision itself is the security-critical part: a timer that selects the
/// highlighted option, a remembered previous answer, and a broken AI checker must all be provable
/// without a console. <see cref="ConsoleConfirmationUi"/> is the production implementation;
/// callers accept this interface so tests can answer instead of a person.
/// </summary>
public interface IConfirmationUi
{
    /// <summary>Ask for a decision on <paramref name="command"/>.</summary>
    /// <param name="timeoutMs">How long the timer runs before the highlighted choice wins.</param>
    /// <param name="noAutoSelect">
    /// Disable the timer entirely: the prompt waits for a real keystroke. Set this whenever nothing
    /// else could decide, so that silence can never be read as consent.
    /// </param>
    /// <param name="note">Short explanation shown before the command (why the human is being asked).</param>
    Task<Choice> AskAsync(
        string command,
        int timeoutMs,
        IKVStore? kvStore,
        string? agentId,
        bool isSubAgent,
        bool noAutoSelect,
        string? note,
        CancellationToken ct);
}

/// <summary>Production implementation: the arrow-key prompt on the terminal.</summary>
public sealed class ConsoleConfirmationUi : IConfirmationUi
{
    public static readonly ConsoleConfirmationUi Instance = new();

    public Task<Choice> AskAsync(
        string command,
        int timeoutMs,
        IKVStore? kvStore,
        string? agentId,
        bool isSubAgent,
        bool noAutoSelect,
        string? note,
        CancellationToken ct)
        => InteractiveConfirmation.ShowAsync(
            command, timeoutMs, kvStore, agentId, isSubAgent, ct, noAutoSelect, note);
}
