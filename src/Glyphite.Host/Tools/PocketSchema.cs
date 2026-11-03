using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glyphite.Host.Utils;

namespace Glyphite.Host.Tools;

/// <summary>
/// The text boundary of a pocket tool: argument JSON → <see cref="PocketArg"/>s, template + values →
/// a command line, entry → the model-facing JSON schema / description / human-readable card.
/// Nothing here touches the vault or bash, which is what makes it testable on its own.
/// </summary>
internal static class PocketSchema
{
    /// <summary>Parse a JSON array of arg definitions. Null when the JSON is malformed or a name is invalid; empty array for null/blank input.</summary>
    internal static List<PocketArg>? ParseArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<PocketArg>>(json, PocketStore.JsonOpts) ?? [];
            foreach (var a in list)
            {
                a.Name = (a.Name ?? "").Trim().ToLowerInvariant();
                if (!PocketEntry.IsValidName(a.Name)) return null;
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse the JSON object of arg values passed to a run. Null when it is not a JSON object.</summary>
    internal static Dictionary<string, object?>? ParseArgsObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
            return dict;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Replace <c>{arg}</c> placeholders in the command template with values (shell-quoted unless the
    /// arg is <c>raw</c>). A placeholder preceded by <c>$</c> is left alone, so shell expansions such as
    /// <c>${HOME}</c> survive.
    /// </summary>
    internal static string Substitute(string cmd, Dictionary<string, string?> values, List<PocketArg> args)
    {
        foreach (var arg in args)
        {
            var has = values.TryGetValue(arg.Name, out var v);
            var final = has ? (v ?? "") : (arg.Def ?? "");
            var inserted = arg.Raw ? final : ShellQuote(final);
            cmd = Regex.Replace(cmd, @"(?<!\$)\{" + Regex.Escape(arg.Name) + @"\}", inserted, RegexOptions.IgnoreCase);
        }
        return cmd;
    }

    /// <summary>Shell-quote a value: safe-char runs pass through, everything else gets single-quoted.</summary>
    internal static string ShellQuote(string value)
    {
        if (value.Length == 0) return "''";
        if (Regex.IsMatch(value, @"^[A-Za-z0-9_\-./:=@%+,]+$")) return value;
        return ShellHelper.QuoteSingle(value);
    }

    internal static string? ArgToString(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
        JsonElement je => je.ToString(),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    /// <summary>Placeholders used in the template but missing from the arg definitions — a warning, not an error.</summary>
    internal static List<string> FindUndefinedPlaceholders(PocketEntry entry)
    {
        var defined = new HashSet<string>(entry.Args.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        return Regex.Matches(entry.Cmd, @"(?<!\$)\{([a-z0-9_]+)\}", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value)
            .Where(p => !defined.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Full card of an entry — what the model sees after add/set/list details.</summary>
    internal static string FormatEntry(PocketEntry e, bool global = false, bool shadowed = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  Name:    {e.Name} (src={e.Src})");
        sb.AppendLine($"  Cat:     {e.Category}");
        sb.AppendLine($"  Scope:   {(global ? "global" : "local")}");
        if (shadowed)
            sb.AppendLine("  Shadowed: yes — a local entry with the same name takes precedence for this agent");
        sb.AppendLine(e.Approved
            ? "  Safety:  approved at add"
            : "  Safety:  legacy — checked on each run");
        sb.AppendLine(e.Materialize
            ? $"  Native:  yes — '{PocketEntry.MaterializedName(e.Name)}'"
            : "  Native:  no — pocket only");
        sb.AppendLine($"  Desc:    {e.Desc}");
        sb.AppendLine($"  Cmd:     {e.Cmd}");
        if (!string.IsNullOrEmpty(e.Cwd)) sb.AppendLine($"  Cwd:     {e.Cwd}");

        if (e.Args.Count == 0)
        {
            sb.AppendLine("  Args:    (none)");
        }
        else
        {
            sb.AppendLine("  Args:");
            foreach (var a in e.Args)
            {
                var req = a.Req ? "required" : "optional";
                var def = a.Def is null ? "" : $", default={a.Def}";
                var raw = a.Raw ? ", raw" : "";
                var desc = string.IsNullOrEmpty(a.Desc) ? "" : $" — {a.Desc}";
                sb.AppendLine($"    {a.Name} ({req}{def}{raw}){desc}");
            }
        }

        if (!string.IsNullOrEmpty(e.Created)) sb.AppendLine($"  Created: {e.Created}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Description of the materialized native tool: what it runs and what it takes.</summary>
    internal static string BuildDescription(PocketEntry e, bool global = false)
    {
        var sb = new StringBuilder(e.Desc);
        if (e.Args.Count > 0)
        {
            sb.Append("\nArgs: ");
            sb.Append(string.Join(", ", e.Args.Select(a => a.Req ? $"{a.Name} (required)" : a.Name)));
        }
        sb.Append($"\nRuns: {e.Cmd}");
        if (e.Materialize)
            sb.Append(global
                ? $"\n(Pocket tool — GLOBAL shared tool; native name '{PocketEntry.MaterializedName(e.Name)}'; also run via pocket_run as '{e.Name}')"
                : $"\n(Pocket tool — native name '{PocketEntry.MaterializedName(e.Name)}'; also run via pocket_run as '{e.Name}')");
        return sb.ToString();
    }

    /// <summary>JSON schema of the native tool: every arg is a string, required ones listed.</summary>
    internal static JsonElement BuildSchema(PocketEntry entry)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", "object");

            w.WritePropertyName("properties");
            w.WriteStartObject();
            foreach (var a in entry.Args)
            {
                w.WritePropertyName(a.Name);
                w.WriteStartObject();
                w.WriteString("type", "string");
                if (!string.IsNullOrEmpty(a.Desc)) w.WriteString("description", a.Desc);
                if (a.Def is not null) w.WriteString("default", a.Def);
                w.WriteEndObject();
            }
            w.WriteEndObject();

            var required = entry.Args.Where(a => a.Req).Select(a => a.Name).ToList();
            if (required.Count > 0)
            {
                w.WritePropertyName("required");
                w.WriteStartArray();
                foreach (var r in required) w.WriteStringValue(r);
                w.WriteEndArray();
            }

            w.WriteBoolean("additionalProperties", false);
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }
}
