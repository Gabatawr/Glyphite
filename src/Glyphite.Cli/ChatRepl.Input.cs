using System.Diagnostics;
using Glyphite.Abstractions.Models;

namespace Glyphite.Cli;

public partial class ChatRepl
{
    private static readonly string[] _knownCommands =
        ["/new", "/clone", "/use", "/delete", "/stats", "/version", "/models", "/compression", "/exit"];

    private int _historyIndex = -1;
    private string? _pendingInput;
    private int _lastBufferLen;

    // Input area geometry, kept in rows *relative to the prompt* — see "Display" at the bottom.
    private int _promptRows = 1;  // how many visual rows the input area currently occupies
    private int _cursorRow;       // 0-based row of the cursor inside that area

    private string? _tabCompletionPrefix;  // partial text that triggered completion
    private int _tabCompletionIndex;       // current index in matches

    private string _promptPrefix = "> ";
    private readonly List<(string Text, ConsoleColor Color)> _promptSegments = [];

    private async Task UpdatePromptPrefixAsync()
    {
        // Fresh config each turn (hot-reload)
        var llm = await _cfgService.GetOptionsAsync<LlmOptions>(LlmOptions.Section);
        _contextWindow = llm.ContextWindow;
        _models = llm.Models;

        var compOpts = await _cfgService.GetOptionsAsync<CompressionOptions>(CompressionOptions.Section, AgentId);

        _promptSegments.Clear();
        var def = ConsoleColor.DarkGray;
        var yellow = ConsoleColor.DarkYellow;
        var white = ConsoleColor.White;

        var lastTokens = _lastTurnLastHit + _lastTurnLastMiss;
        if (lastTokens > 0)
        {
            ConsoleColor color = white;

            var status = await _compactionService.EvaluateCompactionStatusAsync(AgentId, _contextWindow);
            if (status.IsThresholdExceeded && status.WillCompact)
                color = status.Mode.Contains("hard") ? ConsoleColor.Red : yellow;
            else if (status.IsThresholdExceeded)
                color = ConsoleColor.Magenta; // context large but no compaction (rare)

            _promptSegments.Add(($"{lastTokens / 1000.0:F1}K", color));
        }

        // Cumulative cost (per-model pricing)
        var currentCost = await GetCurrentCumulativeCostAsync();
        var cumCost = currentCost >= 0.01 ? $"${currentCost:F2}" : currentCost > 0 ? $"${currentCost:F6}" : "";
        if (string.IsNullOrEmpty(cumCost) && _lastTurnHit + _lastTurnMiss > 0)
            cumCost = "$?"; // usage exists but model pricing unknown
        if (!string.IsNullOrEmpty(cumCost))
            _promptSegments.Add((cumCost, def));

        // +$ = delta of cumulative cost (naturally includes subagent usage)
        if (_prevCumulativeCost >= 0)
        {
            var delta = currentCost - _prevCumulativeCost;
            if (delta > 0)
            {
                var costStr = delta >= 0.01 ? $"${delta:F2}" : $"${delta:F6}";
                _promptSegments.Add(($"+{costStr}", delta >= compOpts.CostSignificantThreshold ? white : def));
            }
        }
        _prevCumulativeCost = currentCost;

        var totalRate = _lastTurnHit + _lastTurnMiss;
        if (totalRate > 0)
        {
            var rate = (int)(_lastTurnHit * 100.0 / totalRate);
            var thr = compOpts.CacheHitRateThreshold;
            var useWhite = rate < thr;
            _promptSegments.Add(($"{rate}%", useWhite ? white : def));
        }

        _promptPrefix = _promptSegments.Count > 0
            ? $"{string.Join(" / ", _promptSegments.Select(s => s.Text))} > "
            : "> ";
    }

    private void UpdatePromptInline(long turnHit, long turnMiss, long turnOutput, long lastHit = 0, long lastMiss = 0)
    {
        _lastTurnHit = turnHit;
        _lastTurnMiss = turnMiss;
        _lastTurnOutput = turnOutput;
        _lastTurnLastHit = lastHit;
        _lastTurnLastMiss = lastMiss;
    }

    /// <summary>After Escape/error, update prompt state from last completed iteration (not total turn — but more accurate than stale values).</summary>
    private void UpdateFromLastIteration()
    {
        if (TurnProcessor.LastIterationUsage is { } usage
            && (usage.TotalHit > 0 || usage.TotalMiss > 0 || usage.TotalOutput > 0))
            UpdatePromptInline(usage.TotalHit, usage.TotalMiss, usage.TotalOutput, usage.LastHit, usage.LastMiss);
    }

    private (double? MissPrice, double? HitPrice, double? OutputPrice) GetPricing(string model)
    {
        foreach (var entry in _models)
            if (string.Equals(entry.Name, model, StringComparison.OrdinalIgnoreCase))
                return (entry.Miss, entry.Hit, entry.Output);
        return (null, null, null);
    }

    /// <summary>Calculate total cumulative cost ($) from all usage rows, using per-model pricing.
    /// Output is billed as-is: the provider's completion count already includes reasoning tokens,
    /// so hidden thinking is paid for here exactly like visible text.</summary>
    private async Task<double> GetCurrentCumulativeCostAsync()
    {
        var usageByModel = await _agentStore.GetUsageByModelAsync(AgentId);
        var total = 0.0;
        foreach (var (modelName, hit, miss, output, _) in usageByModel)
        {
            var (mP, hP, oP) = GetPricing(modelName);
            if (mP.HasValue)
                total += miss * mP.Value + (hP ?? mP.Value) * hit + (oP ?? mP.Value) * output;
        }
        return total / 1_000_000.0;
    }

    private static bool IsCommand(List<char> buffer) => buffer.Count > 0 && buffer[0] == '/';

    // ── Input loop ──

    private async Task<string?> ReadLineWithHistoryAsync()
    {
        var buffer = new List<char>();
        var pos = 0;
        _lastBufferLen = 0;
        _historyIndex = _inputHistory.Count;
        _pendingInput = null;

        // The caller left the cursor at column 0 of a fresh line — the prompt starts there.
        _promptRows = 1;
        _cursorRow = 0;
        Console.Write('\r');
        WriteColoredPrompt();
        Console.ResetColor();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);

            if (ctrl && key.Key == ConsoleKey.C)
                return await HandleCtrlCAsync(buffer);

            if (ctrl && key.Key == ConsoleKey.Z)
                return null;

            if (ctrl && (key.Key == ConsoleKey.W || key.Key == ConsoleKey.H || key.Key == ConsoleKey.Backspace))
            {
                HandleDeleteWord(buffer, ref pos);
                continue;
            }

            var result = HandleKey(buffer, ref pos, key);
            if (result is not null)
                return result;
        }
    }

    /// <summary>Handle a non-control key press. Returns the input string to return, or null to continue the loop.</summary>
    private string? HandleKey(List<char> buffer, ref int pos, ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter: return HandleEnter(buffer);
            case ConsoleKey.Escape: HandleEscape(buffer, ref pos); return null;
            case ConsoleKey.UpArrow: HandleArrowUp(buffer, ref pos); return null;
            case ConsoleKey.DownArrow: HandleArrowDown(buffer, ref pos); return null;
            case ConsoleKey.LeftArrow when key.Modifiers.HasFlag(ConsoleModifiers.Control): HandleWordJumpLeft(buffer, ref pos); return null;
            case ConsoleKey.LeftArrow: HandleCursorLeft(ref pos); return null;
            case ConsoleKey.RightArrow when key.Modifiers.HasFlag(ConsoleModifiers.Control): HandleWordJumpRight(buffer, ref pos); return null;
            case ConsoleKey.RightArrow: HandleCursorRight(buffer, ref pos); return null;
            case ConsoleKey.Home: HandleHome(ref pos); return null;
            case ConsoleKey.End: HandleEnd(buffer, ref pos); return null;
            case ConsoleKey.Backspace: HandleBackspace(buffer, ref pos); return null;
            case ConsoleKey.Delete: HandleDelete(buffer, ref pos); return null;
            case ConsoleKey.Tab: HandleTab(buffer, ref pos); return null;
            default: HandleInsertChar(buffer, ref pos, key); return null;
        }
    }

    // ── Key handlers ──

    private async Task<string?> HandleCtrlCAsync(List<char> buffer)
    {
        var text = new string(buffer.ToArray());
        if (text.Length > 0)
            await CopyToClipboardAsync(text);
        GotoEndOfInput();
        Console.WriteLine();
        return "";
    }

    /// <summary>Copy text to the system clipboard (Ctrl+C UX). Platform commands:
    /// clip (Windows), pbcopy (macOS), wl-copy/xclip/xsel (Linux Wayland/X11). Best-effort.</summary>
    private static async Task CopyToClipboardAsync(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            await RunClipAsync("clip", text);
        }
        else if (OperatingSystem.IsMacOS())
        {
            await RunClipAsync("pbcopy", text);
        }
        else
        {
            if (!await RunClipAsync("wl-copy", text))
                if (!await RunClipAsync("xclip", text, "-selection", "clipboard"))
                    await RunClipAsync("xsel", text, "--clipboard", "--input");
        }
    }

    private static async Task<bool> RunClipAsync(string command, string text, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                RedirectStandardInput = true,
                UseShellExecute = false
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            var proc = Process.Start(psi);
            if (proc is null) return false;

            await proc.StandardInput.WriteAsync(text);
            proc.StandardInput.Close();

            // Don't hang if the clipboard command stalls (e.g. no Wayland session)
            try { await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { try { proc.Kill(entireProcessTree: true); } catch { } }
            return true;
        }
        catch
        {
            return false; // command not available
        }
    }

    private string? HandleEnter(List<char> buffer)
    {
        var input = new string(buffer.ToArray());
        GotoEndOfInput();
        Console.WriteLine();
        if (!string.IsNullOrWhiteSpace(input))
        {
            _inputHistory.Add(input);
            _historyIndex = _inputHistory.Count;
            _pendingInput = null;
        }
        return input;
    }

    private void HandleEscape(List<char> buffer, ref int pos)
    {
        _tabCompletionPrefix = null;
        if (pos < buffer.Count)
        {
            pos = buffer.Count;
            MoveCursor(pos);
        }
        else
        {
            buffer.Clear();
            pos = 0;
            _historyIndex = _inputHistory.Count;
            _pendingInput = null;
            ClearFromPromptToBottom();
            WriteColoredPrompt();
            Console.ResetColor();
            _lastBufferLen = 0;
        }
    }

    private void HandleArrowUp(List<char> buffer, ref int pos)
    {
        if (IsCommand(buffer))
        {
            CompleteCommand(buffer, ref pos, forward: true);
            return;
        }
        if (_historyIndex == _inputHistory.Count)
            _pendingInput = new string(buffer.ToArray());
        for (var i = _historyIndex - 1; i >= 0; i--)
        {
            if ((_inputHistory[i][0] == '/') == false)
            {
                _historyIndex = i;
                buffer.Clear();
                buffer.AddRange(_inputHistory[i]);
                pos = buffer.Count;
                Redraw(_inputHistory[i], pos);
                break;
            }
        }
    }

    private void HandleArrowDown(List<char> buffer, ref int pos)
    {
        if (IsCommand(buffer))
        {
            CompleteCommand(buffer, ref pos, forward: false);
            return;
        }
        for (var i = _historyIndex + 1; i <= _inputHistory.Count; i++)
        {
            if (i == _inputHistory.Count)
            {
                _historyIndex = _inputHistory.Count;
                buffer.Clear();
                buffer.AddRange(_pendingInput ?? "");
            }
            else if ((_inputHistory[i][0] == '/') == false)
            {
                _historyIndex = i;
                buffer.Clear();
                buffer.AddRange(_inputHistory[i]);
            }
            else
            {
                continue;
            }
            pos = buffer.Count;
            Redraw(new string(buffer.ToArray()), pos);
            break;
        }
    }

    private void HandleWordJumpLeft(List<char> buffer, ref int pos)
    {
        WordJumpLeft(buffer, ref pos);
        MoveCursor(pos);
    }

    private void HandleCursorLeft(ref int pos)
    {
        if (pos > 0) { pos--; MoveCursor(pos); }
    }

    private void HandleWordJumpRight(List<char> buffer, ref int pos)
    {
        WordJumpRight(buffer, ref pos);
        MoveCursor(pos);
    }

    private void HandleCursorRight(List<char> buffer, ref int pos)
    {
        if (pos < buffer.Count) { pos++; MoveCursor(pos); }
    }

    private void HandleHome(ref int pos)
    {
        pos = 0;
        MoveCursor(pos);
    }

    private void HandleEnd(List<char> buffer, ref int pos)
    {
        pos = buffer.Count;
        MoveCursor(pos);
    }

    private void HandleBackspace(List<char> buffer, ref int pos)
    {
        _tabCompletionPrefix = null;
        if (pos > 0)
        {
            buffer.RemoveAt(pos - 1);
            pos--;
            Redraw(new string(buffer.ToArray()), pos);
        }
    }

    private void HandleDelete(List<char> buffer, ref int pos)
    {
        _tabCompletionPrefix = null;
        if (pos < buffer.Count)
        {
            buffer.RemoveAt(pos);
            Redraw(new string(buffer.ToArray()), pos);
        }
    }

    private void HandleTab(List<char> buffer, ref int pos)
    {
        CompleteCommand(buffer, ref pos);
    }

    private void HandleInsertChar(List<char> buffer, ref int pos, ConsoleKeyInfo key)
    {
        if (key.KeyChar >= 32 && !char.IsControl(key.KeyChar))
        {
            _tabCompletionPrefix = null;
            buffer.Insert(pos, key.KeyChar);
            pos++;
            Redraw(new string(buffer.ToArray()), pos);
        }
    }

    private void HandleDeleteWord(List<char> buffer, ref int pos)
    {
        DeleteWordBefore(buffer, ref pos);
        Redraw(new string(buffer.ToArray()), pos);
    }

    // ── Tab completion ──

    private void CompleteCommand(List<char> buffer, ref int pos, bool forward = true)
    {
        var text = new string(buffer.ToArray());

        if (text.Length == 0 || text[0] != '/')
            return;

        // New completion session if text changed since last cycle
        if (_tabCompletionPrefix is null || !text.StartsWith(_tabCompletionPrefix))
        {
            _tabCompletionPrefix = text;
            _tabCompletionIndex = -1;
        }

        var matches = _knownCommands.Where(cmd => cmd.StartsWith(_tabCompletionPrefix)).ToArray();
        if (matches.Length == 0)
        {
            _tabCompletionPrefix = null;
            return;
        }

        // First press in this session — start from beginning or end
        if (_tabCompletionIndex < 0)
            _tabCompletionIndex = forward ? 0 : matches.Length - 1;
        else if (forward)
            _tabCompletionIndex = (_tabCompletionIndex + 1) % matches.Length;
        else
            _tabCompletionIndex = (_tabCompletionIndex - 1 + matches.Length) % matches.Length;

        var completed = matches[_tabCompletionIndex];

        buffer.Clear();
        buffer.AddRange(completed);
        pos = completed.Length;
        Redraw(completed, pos);
    }

    // ── Word navigation ──

    private static void DeleteWordBefore(List<char> buffer, ref int pos)
    {
        if (pos == 0) return;
        var end = pos;
        while (pos > 0 && buffer[pos - 1] == ' ') pos--;
        while (pos > 0 && buffer[pos - 1] != ' ') pos--;
        buffer.RemoveRange(pos, end - pos);
    }

    private static void WordJumpLeft(List<char> buffer, ref int pos)
    {
        if (pos == 0) return;
        pos--;
        while (pos > 0 && buffer[pos] == ' ') pos--;
        while (pos > 0 && buffer[pos] != ' ') pos--;
        if (pos > 0 || buffer[0] != ' ') pos++;
    }

    private static void WordJumpRight(List<char> buffer, ref int pos)
    {
        if (pos >= buffer.Count) return;
        while (pos < buffer.Count && buffer[pos] != ' ') pos++;
        while (pos < buffer.Count && buffer[pos] == ' ') pos++;
    }

    // ── Display ──

    // The input area is redrawn with relative cursor moves only. Nothing here may ask the
    // terminal where the cursor is: Console.CursorTop on Unix writes a Device Status Report
    // query (ESC[6n) and then blocks reading the reply from stdin. Lose that reply and the app
    // hangs with the answer on screen and no prompt; consume it late and its own bytes show up
    // as typed text ("[41;1R" after "> "). We wrote every row ourselves, so we can count them.

    private static void MoveUp(int rows)
    {
        if (rows > 0) Console.Write($"\u001b[{rows}A");
    }

    private static void MoveDown(int rows)
    {
        if (rows > 0) Console.Write($"\u001b[{rows}B");
    }

    /// <summary>Erase from the cursor down — everything below the input area is ours.</summary>
    private static void EraseBelow() => Console.Write("\u001b[J");

    /// <summary>Column 0 of the input area's first row.</summary>
    private void GotoPromptStart()
    {
        MoveUp(_cursorRow);
        Console.Write('\r');
        _cursorRow = 0;
    }

    /// <summary>Last row of the input area, so the transcript continues below all of it.</summary>
    private void GotoEndOfInput()
    {
        MoveDown(_promptRows - 1 - _cursorRow);
        Console.Write('\r');
        _cursorRow = _promptRows - 1;
    }

    private void Redraw(string text, int pos = -1)
    {
        var width = Math.Max(1, Console.BufferWidth);
        var textLen = _promptPrefix.Length + text.Length;

        GotoPromptStart();
        WriteColoredPrompt();
        Console.ResetColor();
        Console.Write(text);
        EraseBelow(); // a previously longer input left rows below this one

        // Row of the last written character: terminals wrap lazily, so text that exactly fills
        // its last row still ends on that row instead of starting a new one.
        var endRow = textLen > 0 ? (textLen - 1) / width : 0;
        var endCol = textLen % width;

        var target = pos >= 0 ? _promptPrefix.Length + pos : textLen;
        var targetRow = Math.Min(target / width, endRow);
        var targetCol = target % width;
        if (target == textLen && textLen > 0 && endCol == 0)
            targetCol = width - 1; // the wrap is pending: the cursor visually sits on the last column

        _promptRows = endRow + 1;
        _cursorRow = targetRow;
        _lastBufferLen = text.Length;

        MoveUp(endRow - targetRow);
        Console.Write('\r');
        if (targetCol > 0) Console.Write($"\u001b[{targetCol}C");
    }

    private void ClearFromPromptToBottom()
    {
        GotoPromptStart();
        EraseBelow();
        _promptRows = 1;
    }

    private void MoveCursor(int pos)
    {
        var width = Math.Max(1, Console.BufferWidth);
        var target = _promptPrefix.Length + pos;
        var row = Math.Min(target / width, _promptRows - 1);
        var col = target % width;

        if (row > _cursorRow) MoveDown(row - _cursorRow);
        else MoveUp(_cursorRow - row);
        Console.Write('\r');
        if (col > 0) Console.Write($"\u001b[{col}C");
        _cursorRow = row;
    }

    private void WriteColoredPrompt()
    {
        if (_promptSegments.Count == 0)
        {
            Console.Write("> ");
            return;
        }

        for (var i = 0; i < _promptSegments.Count; i++)
        {
            if (i > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(" / ");
            }
            Console.ForegroundColor = _promptSegments[i].Color;
            Console.Write(_promptSegments[i].Text);
        }
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(" > ");
    }
}
