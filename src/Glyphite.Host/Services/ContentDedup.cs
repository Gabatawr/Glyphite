using System.Text;
using System.Text.RegularExpressions;
using Glyphite.Abstractions.Models;

namespace Glyphite.Host.Services;

public static partial class ContentDedup
{
    private const string EscChar = "\uFEFF";

    public static string Compress(string output, ContentDedupOptions opts)
    {
        if (string.IsNullOrEmpty(output))
            return output;

        output = EscapeLiteralTokens(output);
        output = output.Replace("\r\n", "\n").Replace("\r", "\n");

        var lines = output.Split('\n');
        if (lines.Length < opts.MinLines)
            return UnescapeLiteralTokens(output);

        var totalLen = output.Length;
        var threshold = (int)(totalLen * opts.FrequencyThreshold);
        var freq = new Dictionary<string, (int Count, int LineLen)>();

        foreach (var line in lines)
        {
            if (line.Length < opts.MinLineLength) continue;
            if (freq.TryGetValue(line, out var f))
                freq[line] = (f.Count + 1, f.LineLen);
            else
                freq[line] = (1, line.Length);
        }

        var candidates = freq
            .Where(kv => kv.Value.Count > 1 && kv.Value.Count * kv.Value.LineLen >= threshold)
            .OrderByDescending(kv => kv.Value.Count * kv.Value.LineLen)
            .Take(opts.MaxAliases)
            .ToList();

        if (candidates.Count == 0)
            return UnescapeLiteralTokens(output);

        var aliasMap = new Dictionary<string, string>();
        var idx = 1;
        foreach (var (line, _) in candidates)
            aliasMap[line] = $"{{A{idx++}}}";

        var legend = new StringBuilder();
        legend.AppendLine("[ALIASES]");
        foreach (var (line, alias) in aliasMap)
            legend.AppendLine($"  {alias} = {line.EscapeControl()}");
        legend.AppendLine("[/ALIASES]");

        var body = new StringBuilder();
        var seqAlias = "";
        var seqCount = 0;

        foreach (var line in lines)
        {
            if (aliasMap.TryGetValue(line, out var a))
            {
                if (a == seqAlias)
                {
                    seqCount++;
                    continue;
                }
                FlushSequence(body, seqAlias, seqCount);
                seqAlias = a;
                seqCount = 1;
            }
            else
            {
                FlushSequence(body, seqAlias, seqCount);
                seqAlias = "";
                seqCount = 0;
                body.AppendLine(line);
            }
        }
        FlushSequence(body, seqAlias, seqCount);

        return UnescapeLiteralTokens(legend.ToString().TrimEnd() + "\n" + body.ToString().TrimEnd());
    }

    private static void FlushSequence(StringBuilder sb, string alias, int count)
    {
        if (count == 0) return;
        sb.AppendLine(alias);
        if (count > 1)
            sb.AppendLine($"{{TR:{count - 1}}}");
    }

    private static string EscapeLiteralTokens(string s)
    {
        s = s.Replace("{TR:", "{" + EscChar + "TR:");
        s = s.Replace("{A", "{" + EscChar + "A");
        return s;
    }

    private static string UnescapeLiteralTokens(string s)
    {
        s = s.Replace("{" + EscChar + "TR:", "{TR:");
        s = s.Replace("{" + EscChar + "A", "{A");
        return s;
    }

    private static string EscapeControl(this string s)
    {
        return ControlCharRegex().Replace(s, m => $"\\u{(int)m.Value[0]:x4}");
    }

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]")]
    private static partial Regex ControlCharRegex();
}
