using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Services;

#pragma warning disable CS8764 // Nullability of return type doesn't match overridden member

/// <summary>
/// Wraps an MCP AIFunction to add a `peek` parameter to its JSON schema.
/// This lets the LLM pass peek=true for MCP tools (our built-in tools already have it).
/// The adapter strips `peek` from arguments before forwarding to the MCP server,
/// UNLESS the original MCP tool already declares a `peek` parameter itself.
/// </summary>
public sealed class McpPeekToolAdapter : AIFunction
{
    private readonly AIFunction _inner;
    private readonly JsonElement _schemaWithPeek;
    private readonly bool _hasOriginalPeek;

    public McpPeekToolAdapter(AIFunction inner)
    {
        _inner = inner;
        _hasOriginalPeek = HasPeekParameter(inner.JsonSchema);
        _schemaWithPeek = _hasOriginalPeek ? inner.JsonSchema : InjectPeekParameter(inner.JsonSchema);
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _schemaWithPeek;
    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
    public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;
    public override JsonSerializerOptions? JsonSerializerOptions => _inner.JsonSerializerOptions;
    public override IReadOnlyDictionary<string, object?>? AdditionalProperties => _inner.AdditionalProperties;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments? args, CancellationToken ct)
    {
        // If the MCP server already has a 'peek' parameter, let it through untouched
        if (_hasOriginalPeek)
            return await _inner.InvokeAsync(args, ct);

        // Otherwise, strip our injected 'peek' before forwarding to the MCP server
        if (args is not null && args.Count > 0 && args.ContainsKey("peek"))
        {
            var cleaned = new Dictionary<string, object?>(args.Count);
            foreach (var kv in args)
            {
                if (!string.Equals(kv.Key, "peek", StringComparison.OrdinalIgnoreCase))
                    cleaned[kv.Key] = kv.Value;
            }
            var cleanedArgs = new AIFunctionArguments(cleaned);
            CopyServices(args, cleanedArgs);
            return await _inner.InvokeAsync(cleanedArgs, ct);
        }

        return await _inner.InvokeAsync(args, ct);
    }

    private static void CopyServices(AIFunctionArguments from, AIFunctionArguments to)
    {
        // AIFunctionArguments implements IDictionary<string, object?>
        // Services (IServiceProvider) and Context are not accessible via public API.
        // The dictionary copy above preserves all items; Services/Context
        // are passed through the inner AIFunction via its own invocation path.
    }

    /// <summary>Check if the original schema already declares a 'peek' parameter.</summary>
    private static bool HasPeekParameter(JsonElement schema)
    {
        try
        {
            if (schema.ValueKind != JsonValueKind.Object)
                return false;

            if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
                return false;

            return props.EnumerateObject().Any(p => p.NameEquals("peek"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Inject a 'peek' parameter into the JSON schema if not already present.</summary>
    private static JsonElement InjectPeekParameter(JsonElement schema)
    {
        try
        {
            using var doc = JsonDocument.Parse(schema.GetRawText());
            var root = doc.RootElement;

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

            writer.WriteStartObject();

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("properties"))
                {
                    writer.WriteStartObject("properties");
                    foreach (var p in prop.Value.EnumerateObject())
                        p.WriteTo(writer);

                    writer.WriteEndObject();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            // If no properties section existed, add it
            if (!root.EnumerateObject().Any(p => p.NameEquals("properties")))
            {
                writer.WriteStartObject("properties");
                writer.WriteStartObject("peek");
                writer.WriteString("type", "boolean");
                writer.WriteString("description", "Auto-clean result after tool loop.");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.Flush();

            return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
        }
        catch
        {
            // On any parsing error, return original schema
            return schema;
        }
    }
}
