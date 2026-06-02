using System.ComponentModel;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class FileWriteTool
{
    [Description("Write content to a file, overwriting if it exists. Creates parent directories if they don't exist. The file content is stored in a `file` block for future reference. For targeted edits, prefer `patch_file`. For new files or complete rewrites, use this.")]
    public static async Task<string> WriteFile(
        [Description("Path to the file (absolute or relative to working directory). Parent directories auto-created.")] string path,
        [Description("Complete file content to write. For targeted changes use `patch_file` instead.")] string content,
        [Description("Auto-clean result after tool loop. File is still written.")] bool? peek = null,
        IMemoryStore? store = null,
        string? sessionId = null,
        string? defaultDirectory = null)
    {
        path = OSHelper.NormalizePath(path);
        if (!Path.IsPathRooted(path))
            path = Path.GetFullPath(Path.Combine(defaultDirectory ?? Directory.GetCurrentDirectory(), path));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content);
        return "";
    }

    public static AIFunction AsAIFunction(IMemoryStore store, string sessionId, string? defaultDirectory = null)
        => AIFunctionFactory.Create(
            async (string path, string content, bool? peek = null) =>
                await WriteFile(path, content, peek, store, sessionId, defaultDirectory),
            "write_file");
}
