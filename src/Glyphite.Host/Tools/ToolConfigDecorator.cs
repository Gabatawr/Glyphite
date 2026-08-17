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
            if (contentMaxSize < int.MaxValue && result is string str)
                result = Truncate(str, contentMaxSize, _tmpDir, Name, _agentId);

            return result;
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    /// <summary>
    /// Truncates a tool result string to <paramref name="contentMaxSize"/> chars.
    /// With a tmp dir, saves the full output to a file and shows 1/3 top + 2/3 bottom;
    /// without it, keeps the first N chars and appends a notice.
    /// Shared with the streaming pipeline so re-read file content obeys the same limit.
    /// </summary>
    internal static string Truncate(string result, int contentMaxSize, string tmpDir, string toolName, string? agentId)
    {
        if (contentMaxSize >= int.MaxValue || result.Length <= contentMaxSize)
            return result;

        if (!string.IsNullOrEmpty(tmpDir))
        {
            var agentTmp = Path.Combine(tmpDir, SanitizeForPath(agentId ?? "unknown"));
            Directory.CreateDirectory(agentTmp);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var safeName = SanitizeForPath(toolName);
            var outPath = Path.Combine(agentTmp, $"{safeName}_{timestamp}.out");
            File.WriteAllText(outPath, result);

            // Build truncated view: 1/3 from top + notice + 2/3 from bottom
            var topChars = contentMaxSize / 3;
            var bottomChars = contentMaxSize - topChars;

            ReadOnlySpan<char> span = result.AsSpan();
            var top = span[..topChars];
            var bottom = span[^bottomChars..];

            var note = $"[Output truncated: showing 1/3 ({topChars} chars) and 2/3 ({bottomChars} chars) of {result.Length} total]\n" +
                       $"[Full output saved to: {outPath}]\n";

            return string.Concat(top.ToString(), "\n", note, bottom.ToString());
        }

        // No tmp dir configured — simple truncation
        return result[..contentMaxSize] +
            $"\n\n[Content truncated at {contentMaxSize} chars. Full result length: {result.Length}]";
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
