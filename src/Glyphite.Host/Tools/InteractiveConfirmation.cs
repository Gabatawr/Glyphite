using Glyphite.Abstractions.Interfaces;
using System.Text;

namespace Glyphite.Host.Tools;

/// <summary>
/// Interactive console confirmation prompt with arrow-key navigation and timer auto-select.
/// Single-line format with \r in-place updates — no multi-line positioning issues.
/// Left/Right arrow keys cycle the highlight and disable the timer.
/// Enter confirms the highlighted choice.
/// Timer auto-selects the highlighted choice on expiry.
/// Falls back to <see cref="Choice.Check"/> if console is redirected (non-interactive).
/// Last choice is persisted per-agent in KVStore under "confirmation_last_choice".
/// Uses a single reusable Task.Run(Console.ReadKey) to avoid accumulating
/// abandoned read tasks that steal keystrokes from the active reader.
/// </summary>
internal static class InteractiveConfirmation
{
    public enum Choice { Ok, Stop, Check }

    private static readonly string[] ChoiceLabels = ["OK", "Stop", "Check"];
    private const string LastChoiceKey = "confirmation_last_choice";

    /// <summary>
    /// Show an interactive confirmation prompt and wait for user input.
    /// Single-line with \r in-place updates.
    /// Saves the selected choice per-agent via KVStore for next prompt.
    /// Uses a single reusable <c>Task.Run(Console.ReadKey)</c> — only one pending
    /// read task exists at any time, so no keystroke-stealing between abandoned tasks.
    /// </summary>
    public static async Task<Choice> ShowAsync(
        string command,
        int timeoutMs,
        IKVStore? kvStore,
        string? agentId,
        bool isSubAgent,
        CancellationToken ct)
    {
        // Non-interactive fallback
        if (isSubAgent || Console.IsInputRedirected || Console.IsOutputRedirected)
            return Choice.Check;

        var displayCmd = Sanitize(command);
        var choices = (Choice[])Enum.GetValues(typeof(Choice));

        // Load last choice from KVStore
        var selected = await LoadLastChoiceAsync(kvStore, agentId, choices);
        var timerActive = true;
        var startTime = Environment.TickCount64;

        // Write initial line
        Console.Error.Write(BuildPrompt(displayCmd, choices, selected, timerActive, timeoutMs, startTime));

        // Single reusable read task — created once, nulled after a successful read,
        // so at most ONE Task.Run(Console.ReadKey) hangs in the thread pool.
        Task<ConsoleKeyInfo>? pendingRead = null;

        try
        {
            while (true)
            {
                pendingRead ??= Task.Run(() => Console.ReadKey(true));

                // ── Calculate delay ──
                int delayMs;
                if (timerActive)
                {
                    var elapsed = (int)(Environment.TickCount64 - startTime);
                    var remaining = timeoutMs - elapsed;
                    if (remaining <= 0)
                    {
                        // Timer expired — auto-select
                        var result = choices[selected];
                        await SaveLastChoiceAsync(kvStore, agentId, result);
                        ClearLine();
                        return result;
                    }
                    delayMs = Math.Min(200, remaining);
                }
                else
                {
                    delayMs = 200;
                }

                // ── Wait for EITHER a keypress OR timer tick ──
                var completed = await Task.WhenAny(pendingRead, Task.Delay(delayMs, ct));

                if (completed == pendingRead)
                {
                    var key = (await pendingRead).Key;
                    pendingRead = null; // consumed — next iteration will create fresh

                    switch (key)
                    {
                        case ConsoleKey.LeftArrow:
                            if (timerActive) timerActive = false;
                            MovetoPreviousChoice(choices, ref selected);
                            Console.Error.Write('\r' + BuildPrompt(displayCmd, choices, selected, false, timeoutMs, startTime));
                            break;

                        case ConsoleKey.RightArrow:
                            if (timerActive) timerActive = false;
                            MoveToNextChoice(choices, ref selected);
                            Console.Error.Write('\r' + BuildPrompt(displayCmd, choices, selected, false, timeoutMs, startTime));
                            break;

                        case ConsoleKey.Enter:
                            var enterResult = choices[selected];
                            await SaveLastChoiceAsync(kvStore, agentId, enterResult);
                            ClearLine();
                            return enterResult;
                    }
                }
                else
                {
                    // Timer tick — update display
                    if (timerActive)
                    {
                        var elapsed = (int)(Environment.TickCount64 - startTime);
                        var remaining = Math.Max(0, timeoutMs - elapsed);

                        if (remaining > 0)
                        {
                            Console.Error.Write('\r' + BuildPrompt(displayCmd, choices, selected, true, remaining, startTime));
                        }
                        else
                        {
                            var result = choices[selected];
                            await SaveLastChoiceAsync(kvStore, agentId, result);
                            ClearLine();
                            return result;
                        }
                    }
                }
            }
        }
        finally
        {
            // pendingRead may still be hanging — that's fine, one thread in pool.
            if (!ct.IsCancellationRequested)
                ClearLine();
        }
    }

    /// <summary>Build the single-line prompt string (no trailing \r needed).</summary>
    private static string BuildPrompt(
        string command,
        Choice[] choices,
        int selected,
        bool timerActive,
        int timeoutMs,
        long startTime)
    {
        var sb = new StringBuilder();

        // Timer or hint first (fixed width — stable left side)
        if (timerActive)
        {
            var remaining = Math.Max(1, timeoutMs / 1000);
            sb.Append("· \x1b[2m").Append(remaining).Append("s\x1b[22m ⚠ ");
        }
        else
        {
            sb.Append("· \x1b[2m←→\x1b[22m ⚠ ");
        }

        // Choice bar (fixed width)
        for (int i = 0; i < choices.Length; i++)
        {
            if (i > 0) sb.Append("  ");
            if (i == selected)
                sb.Append("\x1b[7m ").Append(ChoiceLabels[(int)choices[i]]).Append(" \x1b[27m");
            else
                sb.Append(' ').Append(ChoiceLabels[(int)choices[i]]).Append(' ');
        }

        // Command at the end (variable width — right side, no flicker on left)
        sb.Append(" · ");
        sb.Append(command);

        // Right-pad to full terminal width to overwrite any previous longer content
        var line = sb.ToString();
        var width = Console.WindowWidth - 1; // -1 to avoid wrapping
        if (line.Length < width)
            line = line.PadRight(width);
        else if (line.Length > width)
            line = line[..width];

        return line;
    }

    private static void ClearLine()
    {
        var width = Console.WindowWidth - 1;
        Console.Error.Write('\r' + new string(' ', width) + '\r');
    }

    private static void MovetoPreviousChoice(Choice[] choices, ref int selected)
    {
        selected = selected > 0 ? selected - 1 : choices.Length - 1;
    }

    private static void MoveToNextChoice(Choice[] choices, ref int selected)
    {
        selected = (selected + 1) % choices.Length;
    }

    private static string Sanitize(string command)
    {
        var sanitized = command.Replace('\n', ' ').Replace('\r', ' ');
        return sanitized.Length > 80 ? sanitized[..77] + "..." : sanitized;
    }

    private static async Task<int> LoadLastChoiceAsync(
        IKVStore? kvStore, string? agentId, Choice[] choices)
    {
        if (kvStore is null || string.IsNullOrEmpty(agentId))
            return 2; // Default: Check

        try
        {
            var lastChoiceStr = await kvStore.GetAsync(agentId, LastChoiceKey);
            if (string.IsNullOrEmpty(lastChoiceStr))
                return 2; // Default: Check

            if (Enum.TryParse<Choice>(lastChoiceStr, out var lastChoice))
            {
                var idx = Array.IndexOf(choices, lastChoice);
                return idx >= 0 ? idx : 2;
            }
        }
        catch
        {
            // Ignore KVStore errors — fall back to default
        }
        return 2; // Default: Check
    }

    private static async Task SaveLastChoiceAsync(
        IKVStore? kvStore, string? agentId, Choice choice)
    {
        if (kvStore is null || string.IsNullOrEmpty(agentId))
            return;

        try
        {
            await kvStore.SetAsync(agentId, LastChoiceKey, choice.ToString());
        }
        catch
        {
            // Ignore KVStore errors — best-effort persistence
        }
    }
}
