using System.Text;
using System.Text.RegularExpressions;

namespace Glyphite.Cli.Services;

/// <summary>
/// Detects markdown tables in text and renders them as formatted console tables
/// with proportional column widths, centered headers, and multi-line cell support.
/// </summary>
public static partial class TableRenderer
{
    /// <summary>
    /// Processes text: finds markdown tables and replaces them with formatted versions.
    /// Non-table text is left unchanged.
    /// </summary>
    public static string RenderTables(string text)
    {
        var lines = text.Split('\n');
        var result = new StringBuilder(text.Length * 2); // pre-allocate
        var tableLines = new List<string>();
        var inTable = false;

        foreach (var line in lines)
        {
            if (IsTableRow(line))
            {
                tableLines.Add(line);
                inTable = true;
            }
            else
            {
                if (inTable)
                {
                    result.Append(FormatTable(tableLines));
                    tableLines.Clear();
                    inTable = false;
                }
                result.Append(line);
                result.Append('\n');
            }
        }

        if (inTable)
            result.Append(FormatTable(tableLines));

        return result.ToString();
    }

    /// <summary>
    /// Processes text for streaming: returns formatted table or null if not a table context.
    /// Used by streaming renderer when complete lines are available.
    /// </summary>
    public static string? TryFormatTable(List<string> tableLines)
    {
        if (tableLines.Count < 2) return null;
        return FormatTable(tableLines);
    }

    /// <summary>Checks if a line looks like a markdown table row (starts and ends with |).</summary>
    public static bool IsTableRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith('|') && trimmed.EndsWith('|')
            && trimmed.Count(c => c == '|') >= 3; // at least 2 columns = 3 pipes
    }

    /// <summary>Checks if a line is a markdown table separator (|---| or ---).</summary>
    public static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim().Replace("|", "");
        return trimmed.Length > 0 && trimmed.All(c => c is '-' or ':' or ' ');
    }

    private static string FormatTable(List<string> tableLines)
    {
        if (tableLines.Count < 2) return string.Join('\n', tableLines) + '\n';

        // Parse header (line 0), separator (line 1), rows (line 2+)
        var headerCells = ParseRow(tableLines[0]);
        if (headerCells.Count < 2) return string.Join('\n', tableLines) + '\n';

        var separatorLine = tableLines[1].Trim();
        if (!IsTableSeparator(separatorLine))
            return string.Join('\n', tableLines) + '\n';

        var alignments = ParseAlignments(separatorLine);
        var colCount = headerCells.Count;

        var rows = new List<List<string>>();
        for (var i = 2; i < tableLines.Count; i++)
        {
            var row = ParseRow(tableLines[i]);
            if (row.Count > 0)
                rows.Add(row);
        }

        // ── Calculate proportional column widths ──

        var termWidth = Console.WindowWidth;
        var maxTableWidth = termWidth - 2; // 1-char margin each side

        // Measure max LINE width per column (not total cell width).
        // This ensures proportional distribution reflects actual display width,
        // not multi-line wrapped content length.
        var contentWidths = new int[colCount];
        for (var i = 0; i < colCount; i++)
        {
            var maxLine = 0;
            foreach (var line in headerCells[i].Split('\n'))
            {
                var len = GetVisibleLength(line);
                if (len > maxLine) maxLine = len;
            }
            contentWidths[i] = maxLine;
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < Math.Min(row.Count, colCount); i++)
            {
                var maxLine = 0;
                foreach (var line in row[i].Split('\n'))
                {
                    var len = GetVisibleLength(line);
                    if (len > maxLine) maxLine = len;
                }
                if (maxLine > contentWidths[i])
                    contentWidths[i] = maxLine;
            }
        }

        // Total content width + borders (| + 1 space each side + |)
        var totalContentWidth = contentWidths.Sum();
        var bordersWidth = colCount * 3 + 1; // | space ... space | per column + leading |
        var totalNeeded = totalContentWidth + bordersWidth;

        int[] colWidths;
        if (totalNeeded <= maxTableWidth)
        {
            // Fits naturally — use content widths + padding
            colWidths = contentWidths.Select(w => w + 2).ToArray(); // +2 for padding
        }
        else
        {
            // Scale proportionally to fit terminal.
            // 1. Raw proportional share  2. Clamp to min  3. Adjust to fit exactly
            var available = maxTableWidth - (colCount + 1); // borders only, no padding
            colWidths = new int[colCount];
            const int minCol = 10;

            for (var i = 0; i < colCount; i++)
            {
                var share = (int)((double)contentWidths[i] / totalContentWidth * available);
                if (share < minCol) share = minCol;
                colWidths[i] = share;
            }

            // Adjust to exactly fill available width
            var currentSum = colWidths.Sum();
            var diff = available - currentSum;

            if (diff > 0)
            {
                // Distribute slack proportionally
                for (var i = 0; i < colCount; i++)
                {
                    var extra = (int)((double)colWidths[i] / currentSum * diff);
                    colWidths[i] += extra;
                    diff -= extra;
                }
                // Give any rounding remainder to last column
                if (diff > 0) colWidths[colCount - 1] += diff;
            }
            else if (diff < 0)
            {
                // Scale back proportionally (but not below minCol)
                var surplus = -diff;
                var excessTotal = currentSum - minCol * colCount; // total chars above minimums
                if (excessTotal > 0)
                {
                    for (var i = 0; i < colCount && surplus > 0; i++)
                    {
                        var reduce = colWidths[i] - minCol;
                        if (reduce <= 0) continue;
                        var share = Math.Min(reduce,
                            (int)((double)reduce / excessTotal * surplus));
                        colWidths[i] -= share;
                        surplus -= share;
                    }
                }
                // If still over, take from last column
                if (surplus > 0 && colWidths[colCount - 1] > minCol)
                {
                    var take = Math.Min(surplus, colWidths[colCount - 1] - minCol);
                    colWidths[colCount - 1] -= take;
                }
            }
        }

        // ── Build formatted table ──

        var sb = new StringBuilder();

        // Horizontal rule helper
        string HorizontalRule()
        {
            var rule = new StringBuilder();
            rule.Append('+');
            foreach (var w in colWidths)
            {
                rule.Append(new string('-', w));
                rule.Append('+');
            }
            return rule.ToString();
        }

        // Render a single row of cells (with multi-line support)
        void RenderRow(List<string> cells, string[]? alts = null)
        {
            // Split each cell into lines if content is too wide
            var cellLines = new List<List<string>>();
            var maxLines = 1;
            for (var i = 0; i < colCount; i++)
            {
                var content = i < cells.Count ? cells[i] : "";
                content = i < (alts?.Length ?? 0) ? alts![i] : content;
                var lines = WrapText(content, colWidths[i] - 2);
                cellLines.Add(lines);
                if (lines.Count > maxLines)
                    maxLines = lines.Count;
            }

            for (var lineIdx = 0; lineIdx < maxLines; lineIdx++)
            {
                sb.Append('|');
                for (var i = 0; i < colCount; i++)
                {
                    var lines = cellLines[i];
                    var cellText = lineIdx < lines.Count ? lines[lineIdx] : "";
                    var padding = colWidths[i] - 2 - cellText.Length;
                    var leftPad = padding / 2;
                    var rightPad = padding - leftPad;
                    sb.Append(' ');
                    sb.Append(cellText);
                    sb.Append(new string(' ', Math.Max(0, colWidths[i] - 2 - cellText.Length)));
                    sb.Append(" |");
                }
                sb.AppendLine();
            }
        }

        // ── Top border ──
        sb.AppendLine(HorizontalRule());

        // ── Header (centered) ──
        sb.Append('|');
        for (var i = 0; i < colCount; i++)
        {
            var cell = headerCells[i];
            var contentWidth = colWidths[i] - 2;
            var padding = contentWidth - GetVisibleLength(cell);
            var leftPad = padding / 2;
            var rightPad = padding - leftPad;
            sb.Append(' ');
            sb.Append(new string(' ', Math.Max(0, leftPad)));
            sb.Append(cell);
            sb.Append(new string(' ', Math.Max(0, rightPad)));
            sb.Append(" |");
        }
        sb.AppendLine();

        // ── Header separator (always centered to match centered header) ──
        sb.Append('|');
        for (var i = 0; i < colCount; i++)
        {
            var width = colWidths[i];
            sb.Append(':').Append(new string('-', width - 2)).Append(":|");
        }
        sb.AppendLine();
        // ── Data rows ──
        foreach (var row in rows)
        {
            RenderRow(row);
        }

        // ── Bottom border ──
        sb.AppendLine(HorizontalRule());

        return sb.ToString();
    }

    /// <summary>
    /// Parse a markdown table row into cell values.
    /// Handles leading/trailing pipes and trims whitespace.
    /// </summary>
    private static List<string> ParseRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];

        var cells = new List<string>();
        var depth = 0;
        var current = new StringBuilder();
        var escaped = false;

        foreach (var ch in trimmed)
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                current.Append(ch);
                escaped = true;
                continue;
            }

            if (ch == '|' && depth == 0)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            // Track nested pipes inside code blocks or other constructs
            if (ch == '`' || ch == '(' || ch == '[') depth++;
            if ((ch == '`' || ch == ')' || ch == ']') && depth > 0) depth--;

            current.Append(ch);
        }

        if (current.Length > 0)
            cells.Add(current.ToString().Trim());

        return cells;
    }

    /// <summary>
    /// Parse column alignments from the separator line.
    /// :--- = left, ---: = right, :---: = center
    /// </summary>
    private static string[] ParseAlignments(string separatorLine)
    {
        var cells = ParseRow(separatorLine);
        return cells.Select(c =>
        {
            var trimmed = c.Trim();
            var left = trimmed.StartsWith(':');
            var right = trimmed.EndsWith(':');
            if (left && right) return "center";
            if (right) return "right";
            if (left) return "left";
            return "left";
        }).ToArray();
    }

    /// <summary>
    /// Get visible length of a string (ignoring ANSI escape codes if any).
    /// </summary>
    private static int GetVisibleLength(string text)
    {
        // Strip ANSI escapes for length calculation
        return AnsiEscapeRegex().Replace(text, "").Length;
    }

    /// <summary>
    /// Wrap text to fit within maxWidth, breaking at word boundaries.
    /// </summary>
    private static List<string> WrapText(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return [text ?? ""];

        if (GetVisibleLength(text) <= maxWidth)
            return [text];

        var lines = new List<string>();
        var remaining = text;

        while (remaining.Length > 0)
        {
            var visLen = GetVisibleLength(remaining);
            if (visLen <= maxWidth)
            {
                lines.Add(remaining);
                break;
            }

            // Find break point at word boundary
            var breakPos = -1;
            var lastSpaceBefore = -1;
            var nextSpaceAfter = -1;
            var visCount = 0;

            for (var i = 0; i < remaining.Length; i++)
            {
                if (remaining[i] == '\n')
                {
                    breakPos = i;
                    break;
                }

                if (remaining[i] == ' ')
                {
                    if (visCount < maxWidth)
                        lastSpaceBefore = i;
                    else if (nextSpaceAfter < 0)
                        nextSpaceAfter = i;
                }

                visCount++;
                if (visCount >= maxWidth && breakPos < 0)
                {
                    // Prefer break at a space before the limit
                    if (lastSpaceBefore >= 0)
                        breakPos = lastSpaceBefore;
                    // Otherwise use the next space after the limit
                    else if (nextSpaceAfter >= 0)
                        breakPos = nextSpaceAfter;
                    // No spaces found at all — hard break at maxWidth
                    else
                        breakPos = maxWidth;
                    break;
                }
            }

            if (breakPos <= 0)
                breakPos = maxWidth;

            lines.Add(remaining[..breakPos].TrimEnd());
            remaining = remaining[breakPos..].TrimStart();
        }

        return lines;
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*[a-zA-Z]")]
    private static partial Regex AnsiEscapeRegex();
}
