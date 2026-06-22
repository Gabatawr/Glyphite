using System.ComponentModel;
using System.Text;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static class FileWriteTool
{
    public static async Task<string> WriteFile(
        string path,
        string content,
        string? resultType = null,
        bool? peek = null,
        string? sessionId = null,
        string? defaultDirectory = null)
    {
        path = OSHelper.NormalizePath(path);
        if (!Path.IsPathRooted(path))
            path = Path.GetFullPath(Path.Combine(defaultDirectory ?? Directory.GetCurrentDirectory(), path));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.None))
        using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteAsync(content);
            await writer.FlushAsync();
            fs.Flush(true);
        }

        return (resultType ?? "metadata") == "content"
            ? await File.ReadAllTextAsync(path)
            : $"Written {path} ({new FileInfo(path).Length} bytes)";
    }

    private sealed class WriteInvoker(string? defaultDirectory)
    {
        [Description("Write content to a file, overwriting if it exists. Creates parent directories if they don't exist. For targeted edits, prefer `patch_file`. For new files or complete rewrites, use this.")]
        public async Task<string> Execute(
            [Description("Path to the file (absolute or relative to working directory). Parent directories auto-created.")] string path,
            [Description("Complete file content to write. For targeted changes use `patch_file` instead.")] string content,
            [Description("Result detail level: 'metadata' (default, returns path+size) or 'content' (returns full file content).")] string? resultType = null,
            [Description("Auto-clean result after tool loop (default: true). File is still written. Set false to keep result in visible history.")] bool? peek = true)
            => await WriteFile(path, content, resultType, peek, defaultDirectory: defaultDirectory);
    }

    public static AIFunction AsAIFunction(string? defaultDirectory = null)
        => AIFunctionFactory.Create(
            new WriteInvoker(defaultDirectory).Execute,
            "write_file");
}
