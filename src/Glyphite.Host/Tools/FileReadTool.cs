using System.ComponentModel;
using System.Text;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class FileReadTool
{
    public static async Task<string> ReadFile(
        string path,
        ContentDedupOptions dedupOpts,
        int? offset = null,
        int? limit = null,
        bool? compress = null,
        bool? peek = null,
        string[]? dedupExtensions = null,
        string? defaultDirectory = null
    )
    {
        path = OSHelper.NormalizePath(path);
        if (!Path.IsPathRooted(path))
            path = Path.GetFullPath(Path.Combine(defaultDirectory ?? Directory.GetCurrentDirectory(), path));

        if (!File.Exists(path))
            return $"Error: File not found: {path}";

        var lines = await File.ReadAllLinesAsync(path);

        var autoCompress = compress ?? (dedupExtensions is not null &&
            dedupExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

        if (autoCompress && offset is null && limit is null)
        {
            var raw = string.Join('\n', lines);
            var deduped = ContentDedup.Compress(raw, dedupOpts);
            return $"[File: {path}, {lines.Length} lines total, dedup]\n{deduped}";
        }

        var start = Math.Max(0, (offset ?? 1) - 1);
        var count = limit ?? (lines.Length - start);
        count = Math.Min(count, lines.Length - start);

        if (start >= lines.Length)
            return $"Error: Offset ({offset}) exceeds file length ({lines.Length} lines)";

        var selected = lines[start..(start + count)];
        var sb = new StringBuilder();
        for (int i = 0; i < selected.Length; i++)
            sb.AppendLine($"{start + i + 1,6} | {selected[i]}");

        var total = lines.Length;
        var skipped = start;
        var remaining = total - (start + count);
        var meta = $"[File: {path}, {total} lines total";
        if (skipped > 0) meta += $", skipped {skipped}";
        if (remaining > 0) meta += $", {remaining} more";
        meta += "]";

        sb.Insert(0, meta + "\n");
        return sb.ToString().TrimEnd();
    }

    private sealed class ReadInvoker(IConfigService cfg, string? defaultDirectory, string? sessionId)
    {
        [Description("Read a file with optional line range and auto-dedup. Returns content with line numbers and file metadata (total lines, skipped, remaining). Lines are 1-indexed. Use `offset`+`limit` to read specific sections. Auto-deduplicates repeated lines for .log files (can be overridden with `compress`). Prefer this over bash `cat`/`head`/`tail` for file reading.")]
        public async Task<string> Execute(
            string path,
            [Description("Starting line number, 1-indexed. Omit to read from beginning.")] int? offset = null,
            [Description("Maximum number of lines to return. Omit to read all lines from offset.")] int? limit = null,
            [Description("Deduplicate repeated lines (auto-enabled for .log files, set false to disable).")] bool? compress = null,
            bool? peek = null)
        {
            var dedupOpts = await cfg.GetOptionsAsync<ContentDedupOptions>("ContentDedup", sessionId);
            return await ReadFile(path, dedupOpts, offset, limit, compress, peek, dedupOpts.AutoDedupExtensions, defaultDirectory);
        }
    }

    public static AIFunction AsAIFunction(IConfigService? cfg = null, string? defaultDirectory = null, string? sessionId = null)
        => AIFunctionFactory.Create(
            new ReadInvoker(cfg!, defaultDirectory, sessionId).Execute,
            "read_file");
}
