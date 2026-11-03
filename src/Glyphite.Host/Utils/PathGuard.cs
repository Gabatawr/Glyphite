namespace Glyphite.Host.Utils;

/// <summary>
/// Containment rules for working directories, shared by every entry point that accepts one
/// (<c>bash</c>/<c>bash_back</c> at run time, <c>pocket_add</c> when a template is checked in).
///
/// The old check compared strings after <c>TrimEnd('/')</c>, and two failures came out of that one
/// line:
/// <list type="bullet">
///   <item>A rule of <c>"/"</c> became <c>""</c>, and every absolute path starts with <c>"/"</c>, so
///         forbidding the root forbade <em>every</em> absolute working directory — including the
///         project the agent was asked to work in. That was the shipped default
///         (<c>"/", "/etc", "/boot", "/sys", "/proc", "/dev"</c>), so absolute workdirs never worked.</item>
///   <item><c>../etc</c> does not start with <c>"/etc"</c>, so relative paths walked through the same
///         rule that was too broad for absolute ones.</item>
/// </list>
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// Why <paramref name="directory"/> may not be used, or <c>null</c> when it may.
    ///
    /// Both the directory and every rule are canonicalized with <see cref="Path.GetFullPath(string, string)"/>,
    /// so <c>/etc/../etc/passwd</c> is judged as <c>/etc/passwd</c>. A relative directory resolves
    /// against <paramref name="baseDirectory"/> — the directory the agent's shell actually starts in —
    /// and falls back to the process directory, matching the shell's own last resort.
    ///
    /// A rule matches the directory itself or a path strictly below it, on a path-segment boundary:
    /// <c>/etc</c> covers <c>/etc/passwd</c> but not <c>/etcetera</c>. A rule of <c>/</c> covers only
    /// the root itself — everything is technically below <c>/</c>, but the rule exists to keep the agent
    /// out of the root, not to become a blanket ban on absolute paths.
    /// </summary>
    /// <returns>A clause for the caller's message ("matches blocked path '/etc'") or <c>null</c>.</returns>
    public static string? BlockReason(
        string directory, IEnumerable<string> forbiddenDirectories, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        var basePath = ResolveBase(baseDirectory);

        if (!TryResolve(directory, basePath, out var candidate))
            return "cannot be resolved to a directory";

        foreach (var rule in forbiddenDirectories)
        {
            if (string.IsNullOrWhiteSpace(rule))
                continue;

            // A rule that cannot be resolved cannot match anything — skipping it is honest: the
            // alternative is guessing which paths the user meant.
            if (!TryResolve(rule, basePath, out var forbidden))
                continue;

            if (IsSameOrBelow(candidate, forbidden))
                return $"matches blocked path '{rule}'";
        }

        return null;
    }

    /// <summary>Comparison of path segments: case-sensitive where the filesystem is.</summary>
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsSameOrBelow(string candidate, string forbidden)
    {
        if (string.Equals(candidate, forbidden, Comparison))
            return true;

        // The root has no segment boundary to hang a prefix check on, so it is handled by equality only.
        if (IsRoot(forbidden))
            return false;

        var prefix = Path.EndsInDirectorySeparator(forbidden)
            ? forbidden
            : forbidden + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, Comparison);
    }

    private static bool IsRoot(string path) =>
        string.Equals(path, Path.GetPathRoot(path), Comparison);

    /// <summary>Absolute base for relative paths; a broken configured value falls back to the process directory.</summary>
    private static string ResolveBase(string? baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            try
            {
                return Path.GetFullPath(baseDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // fall through: the caller cannot see why a config value is broken, but a shell would
                // still start somewhere, and that somewhere is the process directory.
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static bool TryResolve(string path, string basePath, out string resolved)
    {
        try
        {
            // TrimEndingDirectorySeparator keeps a root intact ("/" stays "/") — TrimEnd('/') is what
            // turned a root rule into an empty string and made it match everything.
            resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, basePath));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            resolved = string.Empty;
            return false;
        }
    }
}
