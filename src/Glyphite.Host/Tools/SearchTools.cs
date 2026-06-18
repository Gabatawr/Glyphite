using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class SearchTools
{
    [Description("Fast file pattern matching using glob patterns. Returns absolute paths sorted by last modified time (most recent first). Supports ** (recursive), * (single segment), and ? (single char). Examples: \"**/*.cs\", \"src/**/*.ts\", \"*.json\". Use this to find files by name/extension patterns. Faster than `bash find` for this purpose.")]
    public static async Task<string> Glob(
        [Description("Glob pattern, e.g. \"**/*.cs\", \"src/**/*.ts\", \"*.json\". Use ** for recursive search.")] string pattern,
        [Description("The directory to search in. Defaults to current directory.")] string? path = null,
        SearchOptions? opts = null,
        string? defaultDirectory = null,
        [Description("Auto-clean result after tool loop.")] bool? peek = null)
    {
        var searchDir = path ?? defaultDirectory ?? Directory.GetCurrentDirectory();
        searchDir = OSHelper.NormalizePath(searchDir);
        if (!Directory.Exists(searchDir))
            return $"Error: Directory not found: {searchDir}";

        opts ??= new();
        var excluded = new HashSet<string>(opts.ExcludedDirectories, StringComparer.OrdinalIgnoreCase);
        var regex = GlobToRegex(pattern);

        var matches = await Task.Run(() =>
            EnumerateFiles(searchDir, excluded, opts.MaxEnumerationFiles)
                .Select(f => new { Info = new FileInfo(f), Relative = Path.GetRelativePath(searchDir, f).Replace('\\', '/') })
                .Where(f => regex.IsMatch(f.Relative) || regex.IsMatch(Path.GetFileName(f.Relative)))
                .OrderByDescending(f => f.Info.LastWriteTimeUtc)
                .Take(opts.MaxResultCount + 1)
                .ToList());

        if (matches.Count == 0)
            return "No files found";

        var truncated = matches.Count > opts.MaxResultCount;
        var sb = new StringBuilder();
        foreach (var m in matches.Take(opts.MaxResultCount))
            sb.AppendLine(m.Info.FullName);

        if (truncated)
            sb.AppendLine($"[Truncated: more than {opts.MaxResultCount} files matched]");

        return sb.ToString().TrimEnd();
    }

    [Description("Search file contents using a regex pattern. Returns file paths with line numbers and matching lines, sorted by file modification time (most recent first). Supports full .NET regex syntax. Use `include` to filter by file pattern (e.g. \"*.cs\", \"*.{ts,tsx}\"). Ideal for finding code references, imports, function definitions, error messages, or any text in files. Automatically skips binary files and respects excluded directories config.")]
    public static async Task<string> Grep(
        [Description("Regex pattern to search for. Supports .NET regex syntax (case-insensitive by default).")] string pattern,
        [Description("The directory to search in. Defaults to current directory.")] string? path = null,
        [Description("File pattern to filter results, e.g. \"*.cs\", \"*.{ts,tsx}\", \"*.py\". Defaults to all text files.")] string? include = null,
        SearchOptions? opts = null,
        string? defaultDirectory = null,
        [Description("Auto-clean result after tool loop.")] bool? peek = null)
    {
        if (string.IsNullOrEmpty(pattern))
            return "Error: Pattern is required";

        var searchDir = path ?? defaultDirectory ?? Directory.GetCurrentDirectory();
        searchDir = OSHelper.NormalizePath(searchDir);
        if (!Directory.Exists(searchDir))
            return $"Error: Directory not found: {searchDir}";

        opts ??= new();
        var excluded = new HashSet<string>(opts.ExcludedDirectories, StringComparer.OrdinalIgnoreCase);
        var binaryExts = new HashSet<string>(opts.BinaryExtensions, StringComparer.OrdinalIgnoreCase);

        Regex searchRegex;
        try { searchRegex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase); }
        catch (ArgumentException ex) { return $"Error: Invalid regex pattern: {ex.Message}"; }

        var maxMatches = opts.MaxResultCount;
        var results = new List<(string File, int Line, string Text, DateTime Mtime)>();
        var stoppedEarly = false;

        foreach (var filePath in EnumerateFiles(searchDir, excluded, opts.MaxEnumerationFiles))
        {
            if (results.Count >= maxMatches) { stoppedEarly = true; break; }

            var ext = Path.GetExtension(filePath);
            if (binaryExts.Contains(ext)) continue;

            if (!string.IsNullOrEmpty(include))
            {
                var relative = Path.GetRelativePath(searchDir, filePath).Replace('\\', '/');
                var incRegex = GlobToRegex(include);
                if (!incRegex.IsMatch(relative) && !incRegex.IsMatch(Path.GetFileName(relative)))
                    continue;
            }

            FileInfo fi;
            try
            {
                fi = new FileInfo(filePath);
                if (!fi.Exists || fi.Length > opts.MaxTextFileSize) continue;
            }
            catch { continue; }

            // Skip empty files (no matches possible) and FIFOs (Length==0, block on open)
            if (fi.Length == 0) continue;

            if (!await IsTextFileAsync(filePath, binaryExts, opts))
                continue;

            try
            {
                var lines = await File.ReadAllLinesAsync(filePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (results.Count >= maxMatches) { stoppedEarly = true; break; }
                    if (!searchRegex.IsMatch(lines[i])) continue;

                    var lineText = lines[i].Length > opts.MaxLineLength
                        ? lines[i][..opts.MaxLineLength] + "..."
                        : lines[i];
                    results.Add((fi.FullName, i + 1, lineText, fi.LastWriteTimeUtc));
                }
            }
            catch { /* skip unreadable files */ }
        }

        if (results.Count == 0)
            return "No matches found";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} match(es)");

        string? lastFile = null;
        foreach (var (file, line, text, _) in results)
        {
            if (file != lastFile)
            {
                sb.AppendLine();
                sb.AppendLine(file);
                lastFile = file;
            }
            sb.AppendLine($"  Line {line}: {text}");
        }

        if (stoppedEarly)
        {
            sb.AppendLine();
            sb.AppendLine($"[Stopped early: more than {maxMatches} matches — showing results from most recently modified files]");
        }

        return sb.ToString().TrimEnd();
    }

    private static IEnumerable<string> EnumerateFiles(string root, HashSet<string> excludedDirs, int maxFiles = int.MaxValue)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        var files = new List<string>();
        queue.Enqueue(root);
        visited.Add(root);
        while (queue.Count > 0 && files.Count < maxFiles)
        {
            var dir = queue.Dequeue();
            var dirName = Path.GetFileName(dir);
            if (!string.IsNullOrEmpty(dirName) && excludedDirs.Contains(dirName))
                continue;

            try
            {
                files.AddRange(Directory.EnumerateFiles(dir));
            }
            catch { /* skip inaccessible dirs */ }

            if (files.Count >= maxFiles) break;

            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    if (visited.Add(subDir))
                        queue.Enqueue(subDir);
                }
            }
            catch { /* skip inaccessible dirs */ }
        }

        return files;
    }

    private static async Task<bool> IsTextFileAsync(string path, HashSet<string> binaryExts, SearchOptions opts)
    {
        var ext = Path.GetExtension(path);
        if (binaryExts.Contains(ext)) return false;

        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            if (fi.Length > opts.MaxTextFileSize) return false;
            if (fi.Length == 0) return true;

            var buffer = new byte[Math.Min((int)fi.Length, opts.DetectBinarySampleSize)];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, opts.DetectBinarySampleSize, FileOptions.Asynchronous);

            var readTask = fs.ReadExactlyAsync(buffer).AsTask();
            var timeout = Task.Delay(TimeSpan.FromSeconds(2));
            var completed = await Task.WhenAny(readTask, timeout);

            if (completed == timeout)
                return false;

            return !buffer.Contains((byte)0);
        }
        catch { return false; }
    }

    private static Regex GlobToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace("\\*\\*", "__DBLSTAR__")
            .Replace("\\*", "[^/\\\\]*")
            .Replace("\\?", "[^/\\\\]")
            .Replace("__DBLSTAR__", ".*");

        return new Regex($"^{escaped}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    public static AIFunction AsGlobFunction(IConfigService? cfg = null, string? defaultDirectory = null)
        => AIFunctionFactory.Create(
            async (string pattern, string? path = null, bool? peek = null) =>
            {
                var opts = cfg is not null ? await cfg.GetOptionsAsync<SearchOptions>("Search") : new();
                return await Glob(pattern, path, opts, defaultDirectory, peek);
            },
            "search_glob");

    public static AIFunction AsGrepFunction(IConfigService? cfg = null, string? defaultDirectory = null)
        => AIFunctionFactory.Create(
            async (string pattern, string? path = null, string? include = null, bool? peek = null) =>
            {
                var opts = cfg is not null ? await cfg.GetOptionsAsync<SearchOptions>("Search") : new();
                return await Grep(pattern, path, include, opts, defaultDirectory, peek);
            },
            "search_grep");
}
