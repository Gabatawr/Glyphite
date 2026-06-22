using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glyphite.Host.Tools;

public static class SearchTools
{
    public static async Task<string> Glob(
        string pattern,
        string? path = null,
        SearchOptions? opts = null,
        string? defaultDirectory = null,
        bool? peek = null,
        ContentDedupOptions? dedupOpts = null,
        ILogger? logger = null)
    {
        var searchDir = path ?? defaultDirectory ?? Directory.GetCurrentDirectory();
        searchDir = OSHelper.NormalizePath(searchDir);
        if (!Directory.Exists(searchDir))
            return $"Error: Directory not found: {searchDir}";

        opts ??= new();
        var excluded = new HashSet<string>(opts.ExcludedDirectories, StringComparer.OrdinalIgnoreCase);
        var regex = GlobToRegex(pattern);

        var matches = await Task.Run(() =>
            EnumerateFiles(searchDir, excluded, opts.MaxEnumerationFiles, logger)
                .Select(f => new { Info = new FileInfo(f), Relative = Path.GetRelativePath(searchDir, f).Replace('\\', '/') })
                .Where(f => regex.IsMatch(f.Relative))
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

        var result = sb.ToString().TrimEnd();
        return dedupOpts is not null ? ContentDedup.Compress(result, dedupOpts) : result;
    }

    public static async Task<string> Grep(
        string pattern,
        string? path = null,
        string? include = null,
        SearchOptions? opts = null,
        string? defaultDirectory = null,
        bool? peek = null,
        ContentDedupOptions? dedupOpts = null,
        ILogger? logger = null)
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
            catch { logger?.LogWarning("Failed to stat file: {Path}", filePath); continue; }

            // Skip empty files (no matches possible) and FIFOs (Length==0, block on open)
            if (fi.Length == 0) continue;

            if (!await IsTextFileAsync(filePath, binaryExts, opts, logger))
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
            catch { logger?.LogWarning("Failed to read file: {Path}", filePath); /* skip unreadable files */ }
        }

        // Sort by modification time (most recent first), matching the documented behaviour
        results = results.OrderByDescending(r => r.Mtime).ToList();

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

        var result = sb.ToString().TrimEnd();
        return dedupOpts is not null ? ContentDedup.Compress(result, dedupOpts) : result;
    }

    private static IEnumerable<string> EnumerateFiles(string root, HashSet<string> excludedDirs, int maxFiles = int.MaxValue, ILogger? logger = null)
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
            catch { logger?.LogWarning("Failed to enumerate files: {Dir}", dir); /* skip inaccessible dirs */ }

            if (files.Count >= maxFiles) break;

            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    if (visited.Add(subDir))
                        queue.Enqueue(subDir);
                }
            }
            catch { logger?.LogWarning("Failed to enumerate directories: {Dir}", dir); /* skip inaccessible dirs */ }
        }

        return files;
    }

    private static async Task<bool> IsTextFileAsync(string path, HashSet<string> binaryExts, SearchOptions opts, ILogger? logger = null)
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
        catch { logger?.LogWarning("Failed to check text file: {Path}", path); return false; }
    }

    private static Regex GlobToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace("\\*\\*", "__DBLSTAR__")
            .Replace("\\*", "[^/\\\\]*")
            .Replace("\\?", "[^/\\\\]");

        // ** matches everything including empty (zero path segments).
        // Replace **/ → (.*/)? so files in root aren't missed (e.g. "**/*.cs" matches "foo.cs").
        // Replace /** → (/.*)? so trailing ** works (e.g. "src/**" matches "src/foo.cs").
        // Standalone ** → .* matches everything.
        escaped = escaped.Replace("__DBLSTAR__/", "(.*/)?")
                         .Replace("/__DBLSTAR__", "(/.*)?")
                         .Replace("__DBLSTAR__", ".*");

        return new Regex($"^{escaped}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    private sealed class SearchInvoker(IConfigService cfg, string? defaultDirectory, string? sessionId, ILogger logger)
    {
        [Description("Fast file pattern matching using glob patterns. Returns absolute paths sorted by last modified time (most recent first). Supports ** (recursive), * (single segment), and ? (single char). Faster than `bash find` for this purpose.")]
        public async Task<string> Glob(
            [Description("Glob pattern, e.g. \"**/*.cs\", \"src/**/*.ts\", \"*.json\"")] string pattern,
            string? path = null,
            bool? peek = null)
        {
            var opts = await cfg.GetOptionsAsync<SearchOptions>(SearchOptions.Section, sessionId);
            var dedupOpts = await cfg.GetOptionsAsync<ContentDedupOptions>(ContentDedupOptions.Section, sessionId);
            return await SearchTools.Glob(pattern, path, opts, defaultDirectory, peek, dedupOpts, logger);
        }

        [Description("Search file contents using a regex pattern. Returns file paths with line numbers and matching lines, sorted by file modification time (most recent first). Supports full .NET regex syntax. Use `include` to filter by file pattern (e.g. \"*.cs\", \"*.{ts,tsx}\"). Ideal for finding code references, imports, function definitions, error messages.")]
        public async Task<string> Grep(
            [Description("Regex pattern to search for. Supports .NET regex syntax (case-insensitive by default).")] string pattern,
            string? path = null,
            [Description("File pattern to filter results, e.g. \"*.cs\", \"*.{ts,tsx}\", \"*.py\"")] string? include = null,
            bool? peek = null)
        {
            var opts = await cfg.GetOptionsAsync<SearchOptions>(SearchOptions.Section, sessionId);
            var dedupOpts = await cfg.GetOptionsAsync<ContentDedupOptions>(ContentDedupOptions.Section, sessionId);
            return await SearchTools.Grep(pattern, path, include, opts, defaultDirectory, peek, dedupOpts, logger);
        }
    }

    public static AIFunction AsGlobFunction(IConfigService cfg, string? defaultDirectory = null, string? sessionId = null, ILogger? logger = null)
        => AIFunctionFactory.Create(
            new SearchInvoker(cfg, defaultDirectory, sessionId, logger ?? NullLogger.Instance).Glob,
            "search_glob");

    public static AIFunction AsGrepFunction(IConfigService cfg, string? defaultDirectory = null, string? sessionId = null, ILogger? logger = null)
        => AIFunctionFactory.Create(
            new SearchInvoker(cfg, defaultDirectory, sessionId, logger ?? NullLogger.Instance).Grep,
            "search_grep");
}
