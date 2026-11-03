using System.Text.RegularExpressions;

namespace Glyphite.Host.Tools;

/// <summary>Single argument definition of a pocket entry.</summary>
public sealed class PocketArg
{
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    public bool Req { get; set; }
    public string? Def { get; set; }
    public bool Raw { get; set; } // if true, value is inserted without shell quoting
}

/// <summary>
/// A pocket tool entry: alias + command template + arg schema. Serialized as-is into the agent's KV
/// vault (see <see cref="PocketStore"/>), so the property names below are part of the stored format —
/// renaming one silently drops that field from every pocket tool already saved.
/// </summary>
public sealed class PocketEntry
{
    /// <summary>Suffix appended to the native (materialized) tool name so pocket tools are clearly marked, e.g. "git_st" → "git_st_pocket".</summary>
    public const string NativeSuffix = "_pocket";

    private const string NamePattern = "^[a-z0-9_]{1,64}$";
    private const string CategoryPattern = "^[a-z0-9_-]{1,64}$";

    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Cmd { get; set; } = "";
    public string? Cwd { get; set; }
    public string Src { get; set; } = "cmd";
    public string Category { get; set; } = "common";
    public List<PocketArg> Args { get; set; } = [];
    public string Created { get; set; } = "";

    /// <summary>True after the command template passed the add-time safety check (forbidden checks + interactive confirmation).</summary>
    public bool Approved { get; set; }

    /// <summary>If true, this entry (favorite) is materialized as a native typed tool in the agent's toolset.</summary>
    public bool Materialize { get; set; }

    public static string NormalizeName(string? name) => (name ?? "").Trim().ToLowerInvariant();

    /// <summary>Native (materialized) name for a pocket entry: entry name + <see cref="NativeSuffix"/>.
    /// Names already ending with the suffix are not double-suffixed. Truncated to keep total length ≤ 64.</summary>
    public static string MaterializedName(string name)
    {
        if (name.EndsWith(NativeSuffix, StringComparison.OrdinalIgnoreCase))
            return name;
        const int maxNameLen = 64;
        if (name.Length + NativeSuffix.Length > maxNameLen)
            name = name[..(maxNameLen - NativeSuffix.Length)];
        return name + NativeSuffix;
    }

    /// <summary>Normalize a category: trim, lowercase, empty → "common".</summary>
    public static string NormalizeCategory(string? category)
    {
        var c = (category ?? "").Trim().ToLowerInvariant();
        return c.Length == 0 ? "common" : c;
    }

    public static bool IsValidName(string name) => Regex.IsMatch(name, NamePattern);

    public static bool IsValidCategory(string category) => Regex.IsMatch(category, CategoryPattern);
}
