using System.Runtime.InteropServices;

namespace Glyphite.Host.Utils;

public static class OSHelper
{
    public static string DetectOS()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsLinux()) return "Linux";
        if (OperatingSystem.IsMacOS()) return "macOS";
        return RuntimeInformation.OSDescription;
    }

    /// <summary>Convert Git Bash /mnt/ paths to Windows native paths.</summary>
    public static string NormalizePath(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path))
            return path;

        path = path.Replace('\\', '/');

        // /mnt/c/... → C:\...
        if (path.Length > 5 && path.StartsWith("/mnt/") && path[5] is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
        {
            var drive = char.ToUpperInvariant(path[5]);
            path = $"{drive}:{path[6..]}".Replace('/', '\\');
        }

        return path;
    }

    /// <summary>
    /// Resolve a path the model wrote against the tool's base directory — the one rule for "where does a
    /// relative path point".
    /// </summary>
    /// <param name="path">Raw argument from the model: absolute, <c>~/…</c>, or relative.</param>
    /// <param name="baseDir">The agent's directory (see <c>ToolRegistry</c>); the process cwd when null.</param>
    /// <remarks>
    /// This exists because the same file is touched twice per <c>write_file</c> call: the tool writes it,
    /// and <see cref="TurnProcessor"/> re-reads it to show the model what landed on disk. Resolving the
    /// two independently is how the model ends up being shown a same-named file from another directory,
    /// or the raw tool output. Both paths go through here so they cannot drift apart.
    /// </remarks>
    public static string ResolvePath(string path, string? baseDir)
    {
        path = NormalizePath(path);
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(Path.Combine(baseDir ?? Directory.GetCurrentDirectory(), path));
    }
}