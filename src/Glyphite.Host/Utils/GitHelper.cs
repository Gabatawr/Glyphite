using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Glyphite.Host.Utils;

/// <summary>Utility for checking git availability and parsing git diff status for files.</summary>
internal static partial class GitHelper
{
    private static bool? _gitAvailable;

    /// <summary>Check if git is installed and available on PATH.</summary>
    internal static bool IsGitAvailable()
    {
        if (_gitAvailable.HasValue)
            return _gitAvailable.Value;

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo("git", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            proc.Start();
            proc.WaitForExit(1000);
            _gitAvailable = proc.ExitCode == 0;
        }
        catch
        {
            _gitAvailable = false;
        }

        return _gitAvailable.Value;
    }

    /// <summary>Check if the file's directory is inside a git repository.</summary>
    internal static bool IsInGitRepo(string filePath)
    {
        if (!IsGitAvailable())
            return false;

        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (string.IsNullOrEmpty(dir))
            return false;

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo("git", "rev-parse --show-toplevel")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = dir,
                }
            };
            proc.Start();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Result of parsing git diff for a file.</summary>
    internal sealed record DiffResult(
        /// <summary>Current-file line number → status char ('+' = differs from HEAD, ' ' = unchanged). Null when unavailable.</summary>
        Dictionary<int, char>? LineStatus,
        /// <summary>Lines deleted from HEAD, with old line numbers. Null when no deletions or git delete mode not requested.</summary>
        List<(int OldLineNum, string Content)>? DeletedLines
    );

    /// <summary>
    /// Run <c>git diff HEAD -- &lt;file&gt;</c> and parse the unified diff output.
    /// Returns null if git is unavailable, file is not tracked, or an error occurs.
    /// </summary>
    internal static DiffResult? GetDiff(string filePath)
    {
        if (!IsGitAvailable() || !IsInGitRepo(filePath))
            return null;

        var fullPath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir))
            return null;

        try
        {
            // Run `git diff HEAD -- <file>` to get unified diff with full context
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo("git", $"diff HEAD -- \"{fullPath}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = dir,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                }
            };
            proc.Start();

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null; // no diff (file unchanged, not tracked, or error)

            return ParseUnifiedDiff(output);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse a unified diff and return current-file line status + deleted lines.</summary>
    internal static DiffResult ParseUnifiedDiff(string diff)
    {
        var lineStatus = new Dictionary<int, char>();
        var deletedLines = new List<(int OldLineNum, string Content)>();

        var hunkHeaderRegex = HunKHeaderRegex();

        var lines = diff.Split('\n');
        var inHunk = false;
        var newLineNum = 0;
        var oldLineNum = 0;

        foreach (var raw in lines)
        {
            if (raw.Length == 0)
                continue;

            // Check for hunk header: @@ -oldStart,oldCount +newStart,newCount @@
            var hunkMatch = hunkHeaderRegex.Match(raw);
            if (hunkMatch.Success)
            {
                inHunk = true;
                newLineNum = int.Parse(hunkMatch.Groups[2].Value);
                oldLineNum = int.Parse(hunkMatch.Groups[1].Value);
                continue;
            }

            if (!inHunk)
            {
                // Skip pre-hunk headers (diff --git, ---, +++, index lines)
                if (raw.StartsWith("diff --git ") ||
                    raw.StartsWith("--- ") ||
                    raw.StartsWith("+++ ") ||
                    raw.StartsWith("index "))
                    continue;

                // If not a header and not in a hunk yet, this file might have
                // no changes or be binary — skip
                continue;
            }

            var prefix = raw[0];
            var content = raw.Length > 1 ? raw[1..] : "";

            switch (prefix)
            {
                case ' ':
                    // Context line — unchanged
                    newLineNum++;
                    oldLineNum++;
                    break;

                case '+':
                    // Added/modified line in current file
                    lineStatus[newLineNum] = '+';
                    newLineNum++;
                    break;

                case '-':
                    // Deleted line from old file
                    deletedLines.Add((oldLineNum, content));
                    oldLineNum++;
                    break;

                // case '\\': — no newline at end of file, ignore
            }
        }

        return new DiffResult(
            lineStatus.Count > 0 ? lineStatus : null,
            deletedLines.Count > 0 ? deletedLines : null
        );
    }

    [GeneratedRegex(@"^@@ -(\d+),?\d* \+(\d+),?\d* @@")]
    private static partial Regex HunKHeaderRegex();
}
