using System.Diagnostics;
using Glyphite.Abstractions.Models;

namespace Glyphite.Cli;

public partial class ChatRepl
{
    private static readonly string[] _knownCommands =
        ["/new", "/clone", "/use", "/delete", "/stats", "/version", "/models", "/exit"];

    private int _historyIndex = -1;
    private string? _pendingInput;
    private int _lastBufferLen;
    private int _promptLine;
    private int _maxVisualLine;

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
            var useYellow = lastTokens * 100.0 / _contextWindow >= compOpts.AutoThreshold;
            _promptSegments.Add(($"{lastTokens / 1000.0:F1}K", useYellow ? yellow : white));
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
        var tp = TurnProcessor;
        if (tp.LastIterationTotalHit > 0 || tp.LastIterationTotalMiss > 0 || tp.LastIterationTotalOutput > 0)
            UpdatePromptInline(
                tp.LastIterationTotalHit, tp.LastIterationTotalMiss, tp.LastIterationTotalOutput,
                tp.LastIterationLastHit, tp.LastIterationLastMiss);
    }

    private (double? MissPrice, double? HitPrice, double? OutputPrice) GetPricing(string model)
    {
        foreach (var entry in _models)
            if (string.Equals(entry.Name, model, StringComparison.OrdinalIgnoreCase))
                return (entry.Miss, entry.Hit, entry.Output);
        return (null, null, null);
    }

    /// <summary>Calculate total cumulative cost ($) from all usage rows, using per-model pricing.</summary>
    private async Task<double> GetCurrentCumulativeCostAsync()
    {
        var usageByModel = await _agentStore.GetUsageByModelAsync(AgentId);
        var total = 0.0;
        foreach (var (modelName, hit, miss, output) in usageByModel)
        {
            var (mP, hP, oP) = GetPricing(modelName);
            if (mP.HasValue)
                total += miss * mP.Value + (hP ?? 0) * hit + (oP ?? mP.Value) * output;
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

        _promptLine = Console.CursorTop;
        _maxVisualLine = _promptLine;
        Console.SetCursorPosition(0, _promptLine);
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
        if (text.Length > 0 && OperatingSystem.IsWindows())
        {
            try
            {
                var psi = new ProcessStartInfo("clip")
                {
                    RedirectStandardInput = true,
                    UseShellExecute = false
                };
                var proc = Process.Start(psi);
                if (proc is not null)
                {
                    await proc.StandardInput.WriteAsync(text);
                    proc.StandardInput.Close();
                }
            }
            catch { /* clip not available */ }
        }
        Console.WriteLine();
        return "";
    }

    private string? HandleEnter(List<char> buffer)
    {
        var input = new string(buffer.ToArray());
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
            Console.SetCursorPosition(0, _promptLine);
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

    private void Redraw(string text, int pos = -1)
    {
        var bufWidth = Console.BufferWidth;
        var bufHeight = Console.BufferHeight;
        var promptLen = _promptPrefix.Length + text.Length;
        var lineCount = (promptLen + bufWidth - 1) / bufWidth; // ceil

        // Old visual range to clear
        var oldMaxLine = _maxVisualLine;

        // New range — _promptLine is fixed (input start point),
        // _maxVisualLine computed via ceil
        _maxVisualLine = _promptLine + lineCount - 1;

        // If new text is shorter — clear stale lines below BEFORE writing
        if (_maxVisualLine < oldMaxLine)
        {
            for (var line = _maxVisualLine + 1; line <= oldMaxLine; line++)
            {
                Console.SetCursorPosition(0, line);
                Console.Write(new string(' ', bufWidth));
            }
        }

        // Write: prompt + text with fixed _promptLine
        Console.SetCursorPosition(0, _promptLine);
        WriteColoredPrompt();
        Console.ResetColor();
        Console.Write(text);

        // If text causes terminal scroll (overflowing buffer) —
        // adjust _promptLine by actual cursor position.
        // Otherwise subsequent Redraw calls write on the wrong line
        // and "eat" history above.
        var actualLastLine = Console.CursorTop;
        var expectedLastLine = _promptLine + lineCount - 1;
        if (actualLastLine != expectedLastLine)
        {
            // Scroll moved content up — recalculate _promptLine
            var oldPromptLine = _promptLine;
            _promptLine = actualLastLine - (lineCount - 1);
            if (_promptLine < 0) _promptLine = 0;
            _maxVisualLine = _promptLine + lineCount - 1;

            // If _promptLine shifted down — clear stale rows
            // between old and new position (when scrolling up normally
            // _promptLine becomes smaller than oldPromptLine —
            // nothing to clear there, terminal already shifted content).
            if (_promptLine > oldPromptLine)
            {
                for (var line = _promptLine; line < oldPromptLine && line < bufHeight; line++)
                {
                    Console.SetCursorPosition(0, line);
                    Console.Write(new string(' ', bufWidth));
                }
            }
        }

        // Tail of last line — if text shortened on the same line
        var endCol = promptLen % bufWidth;
        if (endCol > 0)
        {
            Console.SetCursorPosition(endCol, _maxVisualLine);
            Console.Write(new string(' ', bufWidth - endCol));
        }

        _lastBufferLen = text.Length;

        // Cursor to position pos
        if (pos >= 0)
        {
            var col = _promptPrefix.Length + pos;
            var line = _promptLine + col / bufWidth;
            if (line >= bufHeight) line = bufHeight - 1;
            Console.SetCursorPosition(col % bufWidth, line);
        }
    }

    private void ClearFromPromptToBottom()
    {
        var bufWidth = Console.BufferWidth;
        var bufHeight = Console.BufferHeight;
        for (var line = _promptLine; line <= _maxVisualLine && line < bufHeight; line++)
        {
            Console.SetCursorPosition(0, line);
            Console.Write(new string(' ', bufWidth));
        }
        _maxVisualLine = _promptLine;
    }

    private void MoveCursor(int pos)
    {
        var col = _promptPrefix.Length + pos;
        var bufWidth = Console.BufferWidth;
        var bufHeight = Console.BufferHeight;
        var line = _promptLine + col / bufWidth;
        if (line >= bufHeight) line = bufHeight - 1;
        Console.SetCursorPosition(col % bufWidth, line);
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
