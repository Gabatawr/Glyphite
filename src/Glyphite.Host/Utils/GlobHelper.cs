using System.Text.RegularExpressions;

namespace Glyphite.Host.Utils;

/// <summary>Glob-pattern matching helpers. Supports <c>*</c> (any sequence) and <c>?</c> (single char);
/// <paramref name="pathAware"/> adds <c>**</c> (crosses path segments) semantics.</summary>
public static class GlobHelper
{
    public static Regex ToRegex(string pattern, bool pathAware = false)
    {
        var escaped = Regex.Escape(pattern);

        if (pathAware)
        {
            escaped = escaped.Replace("\\*\\*", "__DBLSTAR__")
                .Replace("\\*", "[^/\\\\]*")
                .Replace("\\?", "[^/\\\\]");

            // ** matches everything including empty (zero path segments).
            // **/ → (.*/)? so files in root aren't missed (e.g. "**/*.cs" matches "foo.cs").
            // /** → (/.*)? so trailing ** works (e.g. "src/**" matches "src/foo.cs").
            // standalone ** → .* matches everything.
            escaped = escaped.Replace("__DBLSTAR__/", "(.*/)?")
                             .Replace("/__DBLSTAR__", "(/.*)?")
                             .Replace("__DBLSTAR__", ".*");

            return new Regex($"^{escaped}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        escaped = escaped.Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }
}
