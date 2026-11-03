using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

/// <summary>
/// Wraps an <see cref="AIFunction"/> to inject a single <c>extra_cfg</c> parameter
/// (nullable object) with fields <c>peek</c> (bool), <c>timeout</c> (int seconds),
/// and <c>maxSize</c> (int chars).
/// Defaults are taken from <see cref="ToolExecutionEntry"/> configuration.
/// When <c>extra_cfg</c> is <c>null</c> (or omitted), all fields use their defaults.
///
/// <para>At runtime, enforces the limits:
/// <list type="bullet">
///   <item><c>timeout</c> — creates a <see cref="CancellationTokenSource"/>
///     with the timeout, linked to the caller's token.</item>
///   <item><c>maxSize</c> — truncates the result string if it exceeds the limit.
///     Full output is saved to a temp file, and the truncated view shows
///     1/3 from the top and 2/3 from the bottom.</item>
/// </list>
/// </para>
/// The injected <c>extra_cfg</c> is stripped before forwarding the call downstream.
/// </summary>
public sealed class ToolConfigDecorator : AIFunction
{
    private readonly AIFunction _inner;
    private readonly JsonElement _schemaWithConfig;
    private readonly bool _peekDefault;
    private readonly int _contentMaxSizeDefault;
    private readonly int _timeoutSecondsDefault;
    private readonly string _tmpDir;
    private readonly string? _agentId;

    /// <summary>Name of the single injected parameter.</summary>
    private const string ExtraCfgParamName = "extra_cfg";

    /// <summary>Default peek value for this tool, from config.</summary>
    public bool DefaultPeek => _peekDefault;

    /// <summary>Effective max result size in chars (int.MaxValue = unlimited).</summary>
    public int ContentMaxSize => _contentMaxSizeDefault;

    /// <summary>Default execution timeout in seconds for this tool (0 = unlimited).</summary>
    public int DefaultTimeoutSeconds => _timeoutSecondsDefault == int.MaxValue ? 0 : _timeoutSecondsDefault;

    /// <summary>Default max result size in chars for this tool (-1 = unlimited, 0 = hidden).</summary>
    public int DefaultContentMaxSize => _contentMaxSizeDefault == int.MaxValue ? -1 : _contentMaxSizeDefault;

    /// <summary>Fields of an <c>extra_cfg</c> argument that actually override this tool's defaults.
    /// An explicit value equal to the default is not an override: stored history should show how a call
    /// was made, not restate configuration. Empty when nothing was overridden; unknown keys are ignored
    /// (the schema forbids them, but a malformed call must not corrupt the stored args).</summary>
    public IReadOnlyDictionary<string, object?> OverridesFrom(JsonElement extraCfg)
    {
        var overrides = new Dictionary<string, object?>();
        if (extraCfg.ValueKind != JsonValueKind.Object)
            return overrides;

        if (extraCfg.TryGetProperty("peek", out var peek)
            && peek.ValueKind is JsonValueKind.True or JsonValueKind.False
            && peek.GetBoolean() != _peekDefault)
            overrides["peek"] = peek.GetBoolean();

        // <= 0 means "no timeout" in both the config and the argument, so normalize before comparing.
        if (extraCfg.TryGetProperty("timeout", out var timeout)
            && timeout.TryGetInt32(out var timeoutSeconds)
            && (timeoutSeconds <= 0 ? 0 : timeoutSeconds) != DefaultTimeoutSeconds)
            overrides["timeout"] = timeoutSeconds;

        if (extraCfg.TryGetProperty("maxSize", out var maxSize)
            && maxSize.TryGetInt32(out var maxChars)
            && maxChars != DefaultContentMaxSize)
            overrides["maxSize"] = maxChars;

        return overrides;
    }

    private static readonly HashSet<string> InjectedParamNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ExtraCfgParamName,
    };

    public ToolConfigDecorator(
        AIFunction inner,
        bool peekDefault,
        int contentMaxSizeDefault,
        int timeoutSecondsDefault,
        string tmpDir = "",
        string? agentId = null)
    {
        _inner = inner;
        _peekDefault = peekDefault;
        // -1 = unlimited, 0 = hide, N > 0 = first N chars (runtime sentinel: int.MaxValue)
        _contentMaxSizeDefault = contentMaxSizeDefault == -1 ? int.MaxValue : contentMaxSizeDefault;
        // <= 0 = unlimited timeout (runtime sentinel: int.MaxValue)
        _timeoutSecondsDefault = timeoutSecondsDefault <= 0 ? int.MaxValue : timeoutSecondsDefault;
        _tmpDir = tmpDir;
        _agentId = agentId;
        // Schema shows original values (before sentinel conversion) for LLM clarity
        _schemaWithConfig = InjectExtraCfgParam(inner.JsonSchema,
            _peekDefault, contentMaxSizeDefault, timeoutSecondsDefault);
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _schemaWithConfig;
    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
    public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;
    public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions!;
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties!;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments? args, CancellationToken ct)
    {
        // ── Resolve effective config: extra_cfg ? merge : use defaults ──
        int timeoutSeconds = _timeoutSecondsDefault;
        int contentMaxSize = _contentMaxSizeDefault;
        bool peek = _peekDefault;

        if (args is not null && args.TryGetValue(ExtraCfgParamName, out var cfgObj) && cfgObj is not null)
        {
            if (cfgObj is JsonElement je && je.ValueKind == JsonValueKind.Object)
                ResolveFromJsonElement(je, ref timeoutSeconds, ref contentMaxSize, ref peek);
            else if (cfgObj is IReadOnlyDictionary<string, object?> cfgDict)
                ResolveFromDictionary(cfgDict, ref timeoutSeconds, ref contentMaxSize, ref peek);
        }

        // ── Apply timeout if configured ──
        CancellationToken effectiveCt = ct;
        CancellationTokenSource? timeoutCts = null;
        if (timeoutSeconds > 0 && timeoutSeconds < int.MaxValue)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            effectiveCt = timeoutCts.Token;
        }

        try
        {
            // ── Strip injected extra_cfg from args ──
            AIFunctionArguments? cleanedArgs = null;
            if (args is not null && args.Count > 0 && args.ContainsKey(ExtraCfgParamName))
            {
                var cleaned = new Dictionary<string, object?>(args.Count);
                foreach (var kv in args)
                {
                    if (!InjectedParamNames.Contains(kv.Key))
                        cleaned[kv.Key] = kv.Value;
                }
                cleanedArgs = new AIFunctionArguments(cleaned);
            }

            // ── Invoke the tool with effective cancellation ──
            var result = await _inner.InvokeAsync(cleanedArgs ?? args, effectiveCt);

            // ── Enforce contentMaxSize: 1/3 top + truncation notice + 2/3 bottom ──
            // AIFunctionFactory hands back a JsonElement (string kind), not a string, so a plain
            // `result is string` test matched no tool at all: `maxSize` was dead configuration and
            // one 2.6 MB fetch_web page went into the history untouched, after which every request
            // was rejected on the provider's context limit.
            if (contentMaxSize < int.MaxValue
                && ResultText(result) is { } text
                && text.Length > contentMaxSize)
            {
                result = Truncate(text, contentMaxSize, _tmpDir, Name, _agentId);
            }

            return result;
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    /// <summary>
    /// Text carried by a tool result, whether the tool returned a <see cref="string"/> directly or
    /// the <see cref="JsonElement"/> that <c>AIFunctionFactory</c> produces for one. Null for
    /// anything that is not text — structured results are left alone.
    /// </summary>
    internal static string? ResultText(object? result) => result switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        _ => null,
    };

    /// <summary>
    /// Truncates a tool result so that the <b>whole returned view</b> fits in
    /// <paramref name="contentMaxSize"/> chars. With a tmp dir the full output is written to a file
    /// and the model sees 1/3 from the top plus 2/3 from the bottom around a notice; without one, the
    /// first chars plus a shorter notice.
    ///
    /// The notice is part of the budget, not an addition to it: a tool result is re-sent with every
    /// later request, so "max size" has to be a ceiling. It used to hold only approximately —
    /// <c>maxSize</c> chars of content plus ~120 chars of notice — which for a small limit is a
    /// multiple of the limit.
    /// </summary>
    internal static string Truncate(string result, int contentMaxSize, string tmpDir, string toolName, string? agentId)
    {
        if (contentMaxSize < 0 || contentMaxSize >= int.MaxValue || result.Length <= contentMaxSize)
            return result;

        if (!string.IsNullOrEmpty(tmpDir))
        {
            var agentTmp = Path.Combine(tmpDir, SanitizeForPath(agentId ?? "unknown"));
            Directory.CreateDirectory(agentTmp);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var safeName = SanitizeForPath(toolName);
            var outPath = Path.Combine(agentTmp, $"{safeName}_{timestamp}.out");
            File.WriteAllText(outPath, result);

            // No per-part char counters in the notice: their width would depend on the split, which
            // would depend on the notice length. The total and the path are what the reader needs.
            var note = $"[Output truncated: {result.Length} chars total — showing the first third and the last two thirds]\n" +
                       $"[Full output saved to: {outPath}]\n";

            // 1 char is the "\n" joining the head to the notice.
            var budget = contentMaxSize - note.Length - 1;
            if (budget <= 0)
                return note[..Math.Clamp(contentMaxSize, 0, note.Length)];

            var topChars = budget / 3;
            var bottomChars = budget - topChars;

            ReadOnlySpan<char> span = result.AsSpan();
            return string.Concat(span[..topChars].ToString(), "\n", note, span[^bottomChars..].ToString());
        }

        // No tmp dir configured — the first chars plus as much of the notice as fits.
        var simpleNote = $"\n\n[Content truncated at {contentMaxSize} chars. Full result length: {result.Length}]";
        if (simpleNote.Length >= contentMaxSize)
            return simpleNote[..Math.Clamp(contentMaxSize, 0, simpleNote.Length)];

        return result[..(contentMaxSize - simpleNote.Length)] + simpleNote;
    }

    private static string SanitizeForPath(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.ToString();
    }

    private static void ResolveFromJsonElement(JsonElement je,
        ref int timeoutSeconds, ref int contentMaxSize, ref bool peek)
    {
        if (je.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number)
            timeoutSeconds = t.GetInt32();
        if (je.TryGetProperty("maxSize", out var m) && m.ValueKind == JsonValueKind.Number)
            contentMaxSize = m.GetInt32();
        if (je.TryGetProperty("peek", out var p) && p.ValueKind == JsonValueKind.True)
            peek = true;
        else if (je.TryGetProperty("peek", out p) && p.ValueKind == JsonValueKind.False)
            peek = false;
    }

    private static void ResolveFromDictionary(IReadOnlyDictionary<string, object?> dict,
        ref int timeoutSeconds, ref int contentMaxSize, ref bool peek)
    {
        if (dict.TryGetValue("timeout", out var t) && t is int ti)
            timeoutSeconds = ti;
        else if (dict.TryGetValue("timeout", out t) && t is JsonElement tje && tje.ValueKind == JsonValueKind.Number)
            timeoutSeconds = tje.GetInt32();

        if (dict.TryGetValue("maxSize", out var m) && m is int mi)
            contentMaxSize = mi;
        else if (dict.TryGetValue("maxSize", out m) && m is JsonElement mje && mje.ValueKind == JsonValueKind.Number)
            contentMaxSize = mje.GetInt32();

        if (dict.TryGetValue("peek", out var p) && p is bool pb)
            peek = pb;
        else if (dict.TryGetValue("peek", out p) && p is JsonElement pje && pje.ValueKind == JsonValueKind.True)
            peek = true;
        else if (dict.TryGetValue("peek", out p) && p is JsonElement pje2 && pje2.ValueKind == JsonValueKind.False)
            peek = false;
    }

    // ── Schema injection ───────────────────────────────────────────────

    private static JsonElement InjectExtraCfgParam(
        JsonElement schema,
        bool peekDefault, int maxSizeDefault, int timeoutDefault)
    {
        try
        {
            using var doc = JsonDocument.Parse(schema.GetRawText());
            var root = doc.RootElement;

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

            writer.WriteStartObject();

            bool hadProperties = false;

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("properties"))
                {
                    hadProperties = true;
                    writer.WriteStartObject("properties");

                    // Copy original properties
                    foreach (var p in prop.Value.EnumerateObject())
                        p.WriteTo(writer);

                    // Inject our single extra_cfg object
                    WriteExtraCfgParam(writer, peekDefault, maxSizeDefault, timeoutDefault);

                    writer.WriteEndObject();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            if (!hadProperties)
            {
                writer.WriteStartObject("properties");
                WriteExtraCfgParam(writer, peekDefault, maxSizeDefault, timeoutDefault);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.Flush();

            return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
        }
        catch
        {
            return schema;
        }
    }

    private static void WriteExtraCfgParam(Utf8JsonWriter writer,
        bool peekDefault, int maxSizeDefault, int timeoutDefault)
    {
        writer.WriteStartObject("extra_cfg");
        writer.WriteString("type", "object");
        writer.WriteNull("default");
        writer.WriteString("description",
            "Override tool configuration defaults. All fields optional; omitted fields use config defaults.");

        writer.WriteStartObject("properties");

        writer.WriteStartObject("peek");
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Auto-clean result after tool loop.");
        writer.WriteEndObject();

        writer.WriteStartObject("timeout");
        writer.WriteString("type", "integer");
        writer.WriteString("description", "Timeout in seconds for tool execution.");
        writer.WriteEndObject();

        writer.WriteStartObject("maxSize");
        writer.WriteString("type", "integer");
        writer.WriteString("description", "Maximum allowed content size in characters.");
        writer.WriteEndObject();

        writer.WriteEndObject(); // properties

        writer.WriteBoolean("additionalProperties", false);

        writer.WriteEndObject(); // extra_cfg
    }
}
