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
}
