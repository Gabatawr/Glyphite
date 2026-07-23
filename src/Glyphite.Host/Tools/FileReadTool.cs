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
        int? maxSize,
        int? offset = null,
        int? limit = null,
        bool? compress = null,
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
            var header = $"[File: {path}, {lines.Length} lines total, dedup]\n";
            var result = header + deduped;
            if (maxSize.HasValue && result.Length > maxSize.Value)
                return $"Error: File too large ({result.Length} chars > {maxSize} limit). Use `offset`+`limit` to read specific sections.";
            return result;
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
        var output = sb.ToString().TrimEnd();

        if (maxSize.HasValue && output.Length > maxSize.Value)
            return $"Error: Result too large ({output.Length} chars > {maxSize} limit). Use `offset`+`limit` to read a smaller section.";

        return output;
    }

    private sealed class ReadInvoker(IConfigService cfg, string? defaultDirectory, string? sessionId, int? maxSize)
    {
        [Description("Read a file with optional line range and auto-dedup. Returns content with line numbers and file metadata (total lines, skipped, remaining). Lines are 1-indexed. Use `offset`+`limit` to read specific sections. Auto-deduplicates repeated lines for .log files (can be overridden with `compress`). Prefer this over bash `cat`/`head`/`tail` for file reading. Large files exceeding the configured tool execution limit will return an error — use offset+limit for those.")]
        public async Task<string> Execute(
            string path,
            [Description("Starting line number, 1-indexed. Omit to read from beginning.")] int? offset = null,
            [Description("Maximum number of lines to return. Omit to read all lines from offset.")] int? limit = null,
            [Description("Deduplicate repeated lines (auto-enabled for .log files, set false to disable).")] bool? compress = null)
        {
            var dedupOpts = await cfg.GetOptionsAsync<ContentDedupOptions>(ContentDedupOptions.Section, sessionId);
            return await ReadFile(path, dedupOpts, maxSize, offset, limit, compress, dedupOpts.AutoDedupExtensions, defaultDirectory);
        }
    }

    public static AIFunction AsAIFunction(IConfigService cfg, string? defaultDirectory = null, string? sessionId = null, int? maxSize = null)
        => AIFunctionFactory.Create(
            new ReadInvoker(cfg, defaultDirectory, sessionId, maxSize).Execute,
            "read_file");
}
