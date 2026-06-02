using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class FilePatchTool
{
    [Description("Replace text in a file using multi-strategy fuzzy matching. Supports 4 matching strategies (tried in order): exact match, trimmed match (both sides), whitespace-normalized (collapse repeating spaces/tabs), and indentation-flexible (strip leading whitespace). Returns a unified diff of changes. Use for targeted edits; for large rewrites use `write_file`. For single-line changes this is preferred over `write_file`. Tip: when using Search/Replace blocks from a diff, copy the EXACT text from the file to avoid fuzzy fallback.")]
    public static async Task<string> PatchFile(
        [Description("Path to the file (absolute or relative to working directory)")] string path,
        [Description("Text to find. Try to match exact content from the file (including indentation). Fuzzy fallbacks handle minor whitespace differences.")] string oldString,
        [Description("Replacement text")] string newString,
        [Description("Replace ALL occurrences of oldString (default: false, replaces only first match). Use with caution.")] bool replaceAll = false,
        [Description("Auto-clean result after tool loop.")] bool? peek = null,
        string? defaultDirectory = null)
    {
        if (string.IsNullOrEmpty(oldString))
            return "Error: oldString is required. Use write_file to create a new file or append content.";

        path = OSHelper.NormalizePath(path);
        if (!Path.IsPathRooted(path))
            path = Path.GetFullPath(Path.Combine(defaultDirectory ?? Directory.GetCurrentDirectory(), path));

        if (!File.Exists(path))
            return $"Error: File not found: {path}";

        if (oldString == newString)
            return "Error: oldString and newString are identical вЂ” nothing to change.";

        var content = await File.ReadAllTextAsync(path);
        var lineEnding = DetectLineEnding(content);
        content = NormalizeLineEndings(content);
        var oldNorm = NormalizeLineEndings(oldString);
        var newNorm = NormalizeLineEndings(newString);

        var contentLines = content.Split('\n');
        var searchLines = oldNorm.Split('\n');
        var replaceLines = newNorm.Split('\n');

        var result = FindMatch(contentLines, searchLines);
        if (result == null)
            return $"Error: Could not find matching text in '{path}'. " +
                   "Try copying the exact text from the file including indentation.";

        var (startLine, matchedLineRange, matchedText, isFuzzy) = result.Value;

        var effectiveReplaceLines = isFuzzy
            ? PreserveFormatting(matchedLineRange, replaceLines)
            : replaceLines;

        var replacedText = string.Join('\n', effectiveReplaceLines);
        string newContent;
        int matchCount;

        if (replaceAll)
        {
            matchCount = CountLineOccurrences(contentLines, matchedLineRange, isFuzzy);
            newContent = ReplaceAllLines(contentLines, matchedLineRange, effectiveReplaceLines, isFuzzy);
        }
        else
        {
            matchCount = 1;
            var before = string.Join('\n', contentLines[..startLine]);
            var after = string.Join('\n', contentLines[(startLine + matchedLineRange.Length)..]);
            newContent = before
                       + (before.Length > 0 ? "\n" : "")
                       + replacedText
                       + (after.Length > 0 ? "\n" : "") + after;
        }

        newContent = RestoreLineEndings(newContent, lineEnding);
        await File.WriteAllTextAsync(path, newContent);

        var diff = BuildDiff(contentLines, replacedText, matchedText, startLine, matchedLineRange.Length);

        var charDiff = newContent.Length - content.Length;
        var resultStr = $"Patched {path}: {FormatCount(matchCount, "occurrence")} replaced, " +
                        $"{FormatBytes(Math.Abs(charDiff))} {(charDiff >= 0 ? "added" : "removed")}\n";
        resultStr += diff;
        return resultStr;
    }

    private static string DetectLineEnding(string text)
    {
        var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
        return idx >= 0 ? "\r\n" : "\n";
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n");

    private static string RestoreLineEndings(string text, string lineEnding)
        => lineEnding == "\r\n" ? text.Replace("\n", "\r\n") : text;

    private static (int StartLine, string[] MatchLines, string MatchedText, bool IsFuzzy)? FindMatch(
        string[] contentLines, string[] searchLines)
    {
        // Strategy 1: Exact line match
        var result = FindLineSequence(contentLines, searchLines, (a, b) => a == b);
        if (result.HasValue)
            return (result.Value.startLine, result.Value.matchLines, result.Value.matchedText, false);

        // Strategy 2: Trimmed lines (both sides)
        var trimmedSearch = searchLines.Select(l => l.Trim()).ToArray();
        result = FindLineSequence(contentLines, trimmedSearch,
            (a, b) => a.Trim() == b);
        if (result.HasValue)
            return (result.Value.startLine, result.Value.matchLines, result.Value.matchedText, true);

        // Strategy 3: Whitespace normalized (collapse repeating spaces/tabs)
        var wsNormSearch = searchLines.Select(WhitespaceNormalize).ToArray();
        var wsNormContent = contentLines.Select(WhitespaceNormalize).ToArray();
        result = FindLineSequence(wsNormContent, wsNormSearch,
            (a, b) => a == b);
        if (result.HasValue)
        {
            var start = result.Value.startLine;
            var origLines = contentLines[start..(start + searchLines.Length)];
            return (start, origLines, string.Join('\n', origLines), true);
        }

        // Strategy 4: Indentation flexible
        var strippedSearch = searchLines.Select(StripIndent).ToArray();
        var strippedContent = contentLines.Select(StripIndent).ToArray();
        result = FindLineSequence(strippedContent, strippedSearch,
            (a, b) => a == b);
        if (result.HasValue)
        {
            var start = result.Value.startLine;
            var origLines = contentLines[start..(start + searchLines.Length)];
            return (start, origLines, string.Join('\n', origLines), true);
        }

        return null;
    }

    private static (int startLine, string[] matchLines, string matchedText)? FindLineSequence(
        string[] contentLines, string[] searchLines,
        Func<string, string, bool> comparer)
    {
        for (int i = 0; i <= contentLines.Length - searchLines.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < searchLines.Length; j++)
            {
                if (!comparer(contentLines[i + j], searchLines[j]))
                { match = false; break; }
            }
            if (match)
            {
                var matched = contentLines[i..(i + searchLines.Length)];
                return (i, matched, string.Join('\n', matched));
            }
        }
        return null;
    }

    private static string WhitespaceNormalize(string line)
        => Regex.Replace(line, @"[ \t]+", " ");

    private static string StripIndent(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 ? line : trimmed;
    }

    private static int CountLineOccurrences(string[] contentLines, string[] matchLines, bool isFuzzy)
    {
        if (matchLines.Length == 0) return 0;
        int count = 0;
        for (int i = 0; i <= contentLines.Length - matchLines.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < matchLines.Length; j++)
            {
                var a = contentLines[i + j];
                var b = matchLines[j];
                if (isFuzzy ? a.Trim() != b.Trim() : a != b)
                { match = false; break; }
            }
            if (match) { count++; i += matchLines.Length - 1; }
        }
        return count;
    }

    private static string ReplaceAllLines(string[] contentLines, string[] matchLines, string[] replaceLines, bool isFuzzy)
    {
        var result = new List<string>(contentLines);
        for (int i = 0; i <= result.Count - matchLines.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < matchLines.Length; j++)
            {
                var a = result[i + j];
                var b = matchLines[j];
                if (isFuzzy ? a.Trim() != b.Trim() : a != b)
                { match = false; break; }
            }
            if (match)
            {
                result.RemoveRange(i, matchLines.Length);
                result.InsertRange(i, replaceLines);
                i += replaceLines.Length - 1;
            }
        }
        return string.Join('\n', result);
    }

    private static string[] PreserveFormatting(string[] originalLines, string[] replaceLines)
    {
        var result = new string[replaceLines.Length];
        for (int i = 0; i < replaceLines.Length; i++)
        {
            var orig = originalLines[Math.Min(i, originalLines.Length - 1)];
            var repl = replaceLines[i];
            var haveOwnIndent = Regex.IsMatch(repl, @"^[ \t]");
            var indent = haveOwnIndent ? Regex.Match(repl, @"^[ \t]*").Value : Regex.Match(orig, @"^[ \t]*").Value;
            var trail = Regex.Match(orig, @"[ \t]*$").Value;
            var trimmed = repl.Trim();
            if (trimmed.Length == 0)
                result[i] = "";
            else
                result[i] = indent + trimmed + trail;
        }
        return result;
    }

    private static string BuildDiff(string[] oldLines, string replacedText,
        string matchedText, int startLine, int removedCount)
    {
        var addedLines = replacedText.Split('\n');
        var removedLines = matchedText.Split('\n');
        var context = 2;

        var sb = new StringBuilder();
        sb.AppendLine($"@@ -{startLine + 1},{removedLines.Length} +{startLine + 1},{addedLines.Length} @@");

        var beforeStart = Math.Max(0, startLine - context);
        for (int i = beforeStart; i < startLine; i++)
            if (i < oldLines.Length)
                sb.AppendLine($" {oldLines[i]}");

        foreach (var line in removedLines)
            sb.AppendLine($"-{line}");

        foreach (var line in addedLines)
            sb.AppendLine($"+{line}");

        var afterEnd = Math.Min(oldLines.Length, startLine + removedLines.Length + context);
        for (int i = startLine + removedLines.Length; i < afterEnd; i++)
            if (i < oldLines.Length)
                sb.AppendLine($" {oldLines[i]}");

        return sb.ToString().TrimEnd();
    }

    private static string FormatCount(int count, string noun)
        => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string FormatBytes(long bytes)
        => bytes >= 1024 ? $"{bytes / 1024.0:F1} KB" : $"{bytes} B";

    public static AIFunction AsAIFunction(string? defaultDirectory = null) => AIFunctionFactory.Create(
        async (string path, string oldString, string newString, bool replaceAll = false, bool? peek = null) =>
            await PatchFile(path, oldString, newString, replaceAll, peek, defaultDirectory),
        "patch_file");
}
