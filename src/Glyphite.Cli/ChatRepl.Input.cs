using System.Diagnostics;

namespace Glyphite.Cli;

public partial class ChatRepl
{
    private readonly List<string> _inputHistory = [];
    private int _historyIndex = -1;
    private string? _pendingInput;
    private int _lastBufferLen;
    private int _promptLine;
    private int _maxVisualLine;

    private string _promptPrefix = "> ";
    private readonly List<(string Text, ConsoleColor Color)> _promptSegments = [];

    private async Task UpdatePromptPrefixAsync()
    {
        _promptSegments.Clear();
        var def = ConsoleColor.DarkGray;
        var yellow = ConsoleColor.DarkYellow;
        var white = ConsoleColor.White;

        var lastTokens = _lastTurnLastHit + _lastTurnLastMiss;
        if (lastTokens > 0)
        {
            var useYellow = lastTokens * 100.0 / _deepseek.ContextWindow >= _compressionOpts.AutoThreshold;
            _promptSegments.Add(($"{lastTokens / 1000.0:F1}K", useYellow ? yellow : white));
        }

        // Cumulative cost (per-model pricing)
        var currentCost = await GetCurrentCumulativeCostAsync();
        var cumCost = currentCost >= 0.01 ? $"${currentCost:F2}" : currentCost > 0 ? $"${currentCost:F6}" : "";
        if (!string.IsNullOrEmpty(cumCost))
            _promptSegments.Add((cumCost, def));

        // +$ = delta of cumulative cost (naturally includes subagent usage)
        if (_prevCumulativeCost >= 0)
        {
            var delta = currentCost - _prevCumulativeCost;
            if (delta > 0)
            {
                var costStr = delta >= 0.01 ? $"${delta:F2}" : $"${delta:F6}";
                _promptSegments.Add(($"+{costStr}", delta >= _compressionOpts.CostSignificantThreshold ? white : def));
            }
        }
        _prevCumulativeCost = currentCost;

        var totalRate = _lastTurnHit + _lastTurnMiss;
        if (totalRate > 0)
        {
            var rate = (int)(_lastTurnHit * 100.0 / totalRate);
            var thr = _compressionOpts.CacheHitRateThreshold;
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

    private (double? MissPrice, double? HitPrice, double? OutputPrice) GetPricing(string model)
    {
        foreach (var entry in _deepseek.Models)
            if (string.Equals(entry.Name, model, StringComparison.OrdinalIgnoreCase))
                return (entry.Miss, entry.Hit, entry.Output);
        return (null, null, null);
    }

    /// <summary>Calculate total cumulative cost ($) from all usage rows, using per-model pricing.</summary>
    private async Task<double> GetCurrentCumulativeCostAsync()
    {
        var usageByModel = await _store.GetUsageByModelAsync(_agentId);
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
                    catch { }
                }
                Console.WriteLine();
                return "";
            }

            if (ctrl && key.Key == ConsoleKey.Z)
                return null;

            if ((ctrl && key.Key == ConsoleKey.W) ||
                (ctrl && key.Key == ConsoleKey.H) ||
                (ctrl && key.Key == ConsoleKey.Backspace))
            {
                DeleteWordBefore(buffer, ref pos);
                Redraw(new string(buffer.ToArray()));
                continue;
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    var input = new string(buffer.ToArray());
                    Console.WriteLine();
                    if (!string.IsNullOrWhiteSpace(input))
                    {
                        _inputHistory.Add(input);
                        _historyIndex = _inputHistory.Count;
                        _pendingInput = null;
                    }
                    return input;

                case ConsoleKey.Escape:
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
                    break;

                case ConsoleKey.UpArrow:
                {
                    var isCmd = IsCommand(buffer);
                    if (_historyIndex == _inputHistory.Count)
                        _pendingInput = new string(buffer.ToArray());
                    for (var i = _historyIndex - 1; i >= 0; i--)
                    {
                        if ((_inputHistory[i][0] == '/') == isCmd)
                        {
                            _historyIndex = i;
                            Redraw(_inputHistory[i]);
                            buffer = [.. _inputHistory[i]];
                            pos = buffer.Count;
                            MoveCursor(pos);
                            break;
                        }
                    }
                    break;
                }

                case ConsoleKey.DownArrow:
                {
                    var isCmd = IsCommand(buffer);
                    for (var i = _historyIndex + 1; i <= _inputHistory.Count; i++)
                    {
                        if (i == _inputHistory.Count)
                        {
                            _historyIndex = _inputHistory.Count;
                            Redraw(_pendingInput ?? "");
                            buffer = [.. (_pendingInput ?? "")];
                        }
                        else if ((_inputHistory[i][0] == '/') == isCmd)
                        {
                            _historyIndex = i;
                            Redraw(_inputHistory[i]);
                            buffer = [.. _inputHistory[i]];
                        }
                        else
                        {
                            continue;
                        }
                        pos = buffer.Count;
                        MoveCursor(pos);
                        break;
                    }
                    break;
                }

                case ConsoleKey.LeftArrow when ctrl:
                    WordJumpLeft(buffer, ref pos);
                    MoveCursor(pos);
                    break;

                case ConsoleKey.LeftArrow:
                    if (pos > 0) { pos--; MoveCursor(pos); }
                    break;

                case ConsoleKey.RightArrow when ctrl:
                    WordJumpRight(buffer, ref pos);
                    MoveCursor(pos);
                    break;

                case ConsoleKey.RightArrow:
                    if (pos < buffer.Count) { pos++; MoveCursor(pos); }
                    break;

                case ConsoleKey.Home:
                    pos = 0;
                    MoveCursor(pos);
                    break;

                case ConsoleKey.End:
                    pos = buffer.Count;
                    MoveCursor(pos);
                    break;

                case ConsoleKey.Backspace:
                    if (pos > 0)
                    {
                        buffer.RemoveAt(pos - 1);
                        pos--;
                        Redraw(new string(buffer.ToArray()));
                    }
                    break;

                case ConsoleKey.Delete:
                    if (pos < buffer.Count)
                    {
                        buffer.RemoveAt(pos);
                        Redraw(new string(buffer.ToArray()));
                    }
                    break;

                default:
                    if (key.KeyChar >= 32 && !char.IsControl(key.KeyChar))
                    {
                        buffer.Insert(pos, key.KeyChar);
                        pos++;
                        Redraw(new string(buffer.ToArray()));
                    }
                    break;
            }
        }
    }

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

    private void Redraw(string text)
    {
        var bufWidth = Console.BufferWidth;
        var bufHeight = Console.BufferHeight;
        var promptLen = _promptPrefix.Length + text.Length;

        // Сохраняем старый диапазон ДО перерисовки
        var oldLine = _promptLine;
        var oldMaxLine = _maxVisualLine;

        // Очищаем весь старый диапазон
        for (var line = oldLine; line <= oldMaxLine && line < bufHeight; line++)
        {
            Console.SetCursorPosition(0, line);
            Console.Write(new string(' ', bufWidth));
        }

        Console.SetCursorPosition(0, oldLine);
        WriteColoredPrompt();
        Console.ResetColor();
        Console.Write(text);

        // Обновляем _promptLine и _maxVisualLine под новый текст
        var cursorTop = Console.CursorTop;
        _promptLine = cursorTop - (promptLen > bufWidth ? (promptLen - 1) / bufWidth : 0);
        if (_promptLine < 0) _promptLine = 0;

        var textLines = promptLen / bufWidth + (promptLen % bufWidth > 0 ? 1 : 0);
        _maxVisualLine = _promptLine + textLines - 1;
        if (_maxVisualLine < _promptLine) _maxVisualLine = _promptLine;

        _lastBufferLen = text.Length;
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
