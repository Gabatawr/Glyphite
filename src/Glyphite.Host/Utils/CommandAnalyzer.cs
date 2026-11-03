using System.Text;

namespace Glyphite.Host.Utils;

/// <summary>
/// Splits a shell command the way a shell would, so a blocked pattern is found wherever it actually
/// sits — after <c>;</c>, behind <c>sudo</c>, inside <c>$(…)</c> or <c>bash -c "…"</c>. Comparing only
/// the first word of the whole string let <c>cd /tmp &amp;&amp; sudo reboot</c>,
/// <c>FOO=1 rm -rf ~</c> and <c>bash -c "rm -rf /"</c> through both the forbidden list and the
/// confirmation list.
///
/// Matching is anchored at the **start of a segment** rather than being a raw substring search:
/// <c>echo "rm -rf"</c> and <c>git log --grep "rm -rf"</c> mention a pattern without running it, and
/// hard-blocking those teaches everyone to route around the checker. The bypasses worth catching are
/// separators and wrappers, which is exactly what this unwraps.
///
/// This is a detector, not a sandbox: it reports <em>where</em> a pattern occurs so the caller can ask
/// the human exactly once for the whole command (see <see cref="FindMatch"/>).
/// </summary>
public static class CommandAnalyzer
{
    /// <summary>Guard against pathological input such as <c>$( $( $( … ) ) )</c>.</summary>
    private const int MaxDepth = 8;

    /// <summary>Wrappers that run their argument as a command — the wrapper itself is peeled off.</summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "doas", "env", "nohup", "timeout", "xargs", "nice", "ionice", "stdbuf",
        "command", "exec", "builtin", "setsid", "unbuffer", "time",
    };

    /// <summary>Wrappers whose first non-option argument is not the command (timeout's duration).</summary>
    private static readonly HashSet<string> SkipsFirstArgument = new(StringComparer.OrdinalIgnoreCase)
    {
        "timeout",
    };

    /// <summary>Shells whose <c>-c</c> argument is a command of its own.</summary>
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "zsh", "dash", "ksh", "ash", "busybox",
    };

    /// <summary>Wrapper options that consume the following token as their value.</summary>
    private static readonly Dictionary<string, HashSet<string>> OptionsWithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sudo"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "-u", "-g", "-p", "-C", "-h", "-r", "-t", "-U", "-T", "-R", "-D",
            "--user", "--group", "--prompt", "--close-from", "--host", "--role",
            "--type", "--other-user", "--command-timeout", "--chdir",
        },
        ["doas"] = new(StringComparer.OrdinalIgnoreCase) { "-u", "-C" },
        ["env"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "-u", "-C", "-S", "-P", "--unset", "--chdir", "--split-string",
        },
        ["nice"] = new(StringComparer.OrdinalIgnoreCase) { "-n", "--adjustment" },
        ["ionice"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "-c", "-n", "-p", "-t", "--class", "--classdata", "--pid",
        },
        ["stdbuf"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "-i", "-o", "-e", "--input", "--output", "--error",
        },
        ["xargs"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "-n", "-I", "-P", "-L", "-s", "-d", "-E", "-a",
            "--max-args", "--replace", "--max-procs", "--max-lines", "--max-chars",
            "--delimiter", "--eof", "--arg-file",
        },
        ["timeout"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "-s", "-k", "--signal", "--kill-after",
        },
    };

    /// <summary>Every simple command in the string, with separators, wrappers and substitutions resolved.</summary>
    public static IReadOnlyList<string> Segments(string command)
    {
        var segments = new List<string>();
        Collect(command, segments, 0);
        return segments;
    }

    /// <summary>
    /// First pattern from <paramref name="patterns"/> that occurs as a command of its own anywhere in
    /// <paramref name="command"/>, or null. Callers use one match to decide — a single confirmation for
    /// the whole string, not one per hit.
    /// </summary>
    public static string? FindMatch(string command, IEnumerable<string>? patterns)
    {
        if (patterns is null || string.IsNullOrWhiteSpace(command)) return null;

        var segments = Segments(command);
        if (segments.Count == 0) return null;

        foreach (var pattern in patterns)
        {
            var normalized = Normalize(pattern ?? "");
            if (normalized.Length == 0) continue;

            foreach (var segment in segments)
                if (StartsWithSegment(segment, normalized))
                    return normalized;
        }

        return null;
    }

    /// <summary>True when a segment runs <paramref name="pattern"/> as its command (not as an argument).</summary>
    private static bool StartsWithSegment(string segment, string pattern)
    {
        if (!segment.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            return false;

        // "rm -rf" must not match "rm -rfx": the pattern has to end at a word boundary.
        return segment.Length == pattern.Length || char.IsWhiteSpace(segment[pattern.Length]);
    }

    // ── Scanning ────────────────────────────────────────────────────────────

    private static void Collect(string command, List<string> segments, int depth)
    {
        if (depth > MaxDepth || command.Length == 0) return;

        var buffer = new StringBuilder();

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];

            // Quotes: copy verbatim — a separator inside them is literal text.
            if (c is '\'' or '"')
            {
                var end = SkipQuoted(command, i);
                buffer.Append(command, i, end - i);
                i = end - 1;
                continue;
            }

            // Backtick substitution: whatever it holds runs as a command too.
            if (c == '`')
            {
                var end = command.IndexOf('`', i + 1);
                if (end < 0) end = command.Length;

                Emit(buffer, segments, depth);
                Collect(command[(i + 1)..end], segments, depth + 1);
                i = end;
                continue;
            }

            // $( … ): same, with balanced parentheses.
            if (c == '$' && i + 1 < command.Length && command[i + 1] == '(')
            {
                var end = SkipBalanced(command, i + 1);

                Emit(buffer, segments, depth);
                Collect(command[(i + 2)..end], segments, depth + 1);
                i = end;
                continue;
            }

            if (IsSeparator(command, i))
            {
                Emit(buffer, segments, depth);
                continue;
            }

            buffer.Append(c);
        }

        Emit(buffer, segments, depth);
    }

    private static void Emit(StringBuilder buffer, List<string> segments, int depth)
    {
        var raw = buffer.ToString().Trim();
        buffer.Clear();
        if (raw.Length == 0) return;

        AddSegment(raw, segments, depth);
    }

    /// <summary>Tokenize one simple command, peel wrappers and substitutions, emit what is left.</summary>
    private static void AddSegment(string raw, List<string> segments, int depth)
    {
        var tokens = Tokenize(raw);
        var index = 0;

        while (index < tokens.Count)
        {
            var value = tokens[index].Value;

            // Leading environment assignment: FOO=bar rm -rf ~
            if (IsEnvAssignment(value))
            {
                index++;
                continue;
            }

            var name = CommandName(value);

            // bash -c "…" — the script argument is a command of its own; the outer call is just a shell.
            if (Shells.Contains(name))
            {
                var scriptIndex = FindScriptFlag(tokens, index + 1);
                if (scriptIndex >= 0 && scriptIndex + 1 < tokens.Count)
                    Collect(tokens[scriptIndex + 1].Value, segments, depth + 1);

                return;
            }

            if (!Wrappers.Contains(name)) break;

            index = SkipWrapperOptions(tokens, index + 1, name);

            if (SkipsFirstArgument.Contains(name) && index < tokens.Count && !tokens[index].Value.StartsWith('-'))
                index++;
        }

        if (index >= tokens.Count) return;

        segments.Add(Normalize(string.Join(' ', tokens.Skip(index).Select(t => t.Value))));
    }

    private static int SkipWrapperOptions(List<Token> tokens, int index, string wrapper)
    {
        var withValue = OptionsWithValue.GetValueOrDefault(wrapper);

        while (index < tokens.Count)
        {
            var value = tokens[index].Value;
            if (value.Length < 2 || value[0] != '-') break;

            // --option=value carries its value inline.
            if (value.Contains('='))
            {
                index++;
                continue;
            }

            index += withValue is not null && withValue.Contains(value) ? 2 : 1;
        }

        return index;
    }

    /// <summary>Index of the <c>-c</c> flag (alone or inside combined flags such as <c>-lc</c>).</summary>
    private static int FindScriptFlag(List<Token> tokens, int start)
    {
        for (var i = start; i < tokens.Count; i++)
        {
            var value = tokens[i].Value;
            if (value.Length < 2 || value[0] != '-' || value.StartsWith("--", StringComparison.Ordinal))
                continue;

            if (value.Contains('c')) return i;
        }

        return -1;
    }

    private static bool IsSeparator(string command, int index)
    {
        var c = command[index];

        if (c is not (';' or '\n' or '\r' or '|' or '&' or '(' or ')')) return false;

        // 2>&1 / 1<&2 are redirections, not background jobs.
        if (c == '&' && index > 0 && command[index - 1] is '>' or '<') return false;

        return true;
    }

    /// <summary>Index just past the closing quote at <paramref name="start"/>.</summary>
    private static int SkipQuoted(string text, int start)
    {
        var quote = text[start];
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\' && quote == '"')
            {
                i++;
                continue;
            }

            if (text[i] == quote) return i + 1;
        }

        return text.Length;
    }

    /// <summary>Index of the parenthesis closing the one at <paramref name="open"/>.</summary>
    private static int SkipBalanced(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];

            if (c is '\'' or '"')
            {
                i = SkipQuoted(text, i) - 1;
                continue;
            }

            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return text.Length;
    }

    // ── Tokens ──────────────────────────────────────────────────────────────

    private readonly record struct Token(string Value);

    private static List<Token> Tokenize(string raw)
    {
        var tokens = new List<Token>();
        var current = new StringBuilder();
        var hasToken = false;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];

            if (char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(new Token(current.ToString()));
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                var end = SkipQuoted(raw, i);
                // Strip the outer quotes but keep their content, spaces included.
                var inner = raw[(i + 1)..Math.Max(i + 1, end - 1)];
                current.Append(inner);
                hasToken = true;
                i = end - 1;
                continue;
            }

            if (c == '\\' && i + 1 < raw.Length)
            {
                current.Append(raw[++i]);
                hasToken = true;
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
            tokens.Add(new Token(current.ToString()));

        return tokens;
    }

    /// <summary>Command as the shell sees it: basename, no <c>.exe</c>, no quotes.</summary>
    private static string CommandName(string token)
    {
        var slash = token.LastIndexOfAny(['/', '\\']);
        var name = slash >= 0 ? token[(slash + 1)..] : token;

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static bool IsEnvAssignment(string token)
    {
        var equals = token.IndexOf('=');
        if (equals <= 0) return false;

        for (var i = 0; i < equals; i++)
        {
            var c = token[i];
            var ok = char.IsLetterOrDigit(c) || c == '_';
            if (!ok) return false;
            if (i == 0 && char.IsDigit(c)) return false;
        }

        return true;
    }

    /// <summary>Collapse whitespace so <c>rm  -rf</c> and <c>rm -rf</c> are the same command.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;

        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }
}
