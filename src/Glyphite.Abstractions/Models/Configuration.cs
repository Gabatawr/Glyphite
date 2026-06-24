using System.Linq;

namespace Glyphite.Abstractions.Models;

public class LlmOptions
{
    public const string Section = "LLM";
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public LlmModel[] Models { get; set; } = [];
    public int ContextWindow { get; set; }
    /// <summary>Reasoning effort level: None, Low, Medium, High, ExtraHigh. Case-insensitive.</summary>
    public string? ReasoningEffort { get; set; }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
            throw new InvalidOperationException("LLM:Endpoint is not configured.");
        if (string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException("LLM:Model is not configured.");
        if (ContextWindow <= 0)
            throw new InvalidOperationException("LLM:ContextWindow must be > 0.");
        if (Models.Length == 0)
            throw new InvalidOperationException("LLM:Models must have at least one model entry.");
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException(
                "LLM API key is not configured. Configure it in Glyphite.json under LLM:ApiKey.");
    }
}

public class LlmModel
{
    public string Name { get; set; } = string.Empty;
    public double Miss { get; set; }
    public double Hit { get; set; }
    public double Output { get; set; }
}

public class WebFetchOptions
{
    public const string Section = "WebFetch";
    public int TimeoutSeconds { get; set; }
    public string UserAgent { get; set; } = string.Empty;
    public int MaxContentLength { get; set; }
    public string DefaultFormat { get; set; } = string.Empty;
    public void Validate()
    {
        if (TimeoutSeconds <= 0)
            throw new InvalidOperationException("WebFetch:TimeoutSeconds must be > 0.");
        if (string.IsNullOrWhiteSpace(UserAgent))
            throw new InvalidOperationException("WebFetch:UserAgent is not configured.");
        if (MaxContentLength <= 0)
            throw new InvalidOperationException("WebFetch:MaxContentLength must be > 0.");
        if (string.IsNullOrWhiteSpace(DefaultFormat))
            throw new InvalidOperationException("WebFetch:DefaultFormat is not configured.");
    }
}

public class ContentDedupOptions
{
    public const string Section = "ContentDedup";
    public int MinLines { get; set; }
    public double FrequencyThreshold { get; set; }
    public int MinLineLength { get; set; }
    public int MaxAliases { get; set; }
    public string[] AutoDedupExtensions { get; set; } = [];
    public void Validate()
    {
        if (MinLines <= 0)
            throw new InvalidOperationException("ContentDedup:MinLines must be > 0.");
        if (FrequencyThreshold <= 0 || FrequencyThreshold > 1)
            throw new InvalidOperationException("ContentDedup:FrequencyThreshold must be in (0, 1].");
        if (MinLineLength <= 0)
            throw new InvalidOperationException("ContentDedup:MinLineLength must be > 0.");
        if (MaxAliases <= 0)
            throw new InvalidOperationException("ContentDedup:MaxAliases must be > 0.");
    }
}

public class BashOptions
{
    public const string Section = "Bash";
    public string ExecutablePath { get; set; } = string.Empty;
    public string DefaultDirectory { get; set; } = string.Empty;
    public int DiscoveryTimeoutMs { get; set; }
    public int DefaultTimeoutMs { get; set; }
    /// <summary>Max output chars for bash. Truncated to 1/3+2/3 with full output saved to tmp/.</summary>
    public int MaxOutput { get; set; } = 100_000;
    public string[] AllowedExecutables { get; set; } = [];
    public string[] ForbiddenCommands { get; set; } = [];
    public string[] ForbiddenDirectories { get; set; } = [];
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath))
            throw new InvalidOperationException("Bash:ExecutablePath is not configured.");
        if (DiscoveryTimeoutMs <= 0)
            throw new InvalidOperationException("Bash:DiscoveryTimeoutMs must be > 0.");
        if (DefaultTimeoutMs <= 0)
            throw new InvalidOperationException("Bash:DefaultTimeoutMs must be > 0.");
        if (MaxOutput <= 0)
            throw new InvalidOperationException("Bash:MaxOutput must be > 0.");
    }
}

public class MemoryOptions
{
    public const string Section = "Memory";
    public string[] ProtectedBlockTypes { get; set; } = [];
    /// <summary>If true, cascade-read AGENTS.md (home → parentCwd → agentCwd) and append to system prompt.</summary>
    public bool ReadAgentsFile { get; set; } = false;
    /// <summary>If true, re-read AGENTS.md from disk on every turn. If false, cache in memory.</summary>
    public bool TurnReloadAgentsFile { get; set; } = false;
    /// <summary>If true, re-read Glyphite.{agentId}.md from disk on every turn. If false, cache in memory.</summary>
    public bool TurnReloadNameFile { get; set; } = false;
    public void Validate()
    {
        if (ProtectedBlockTypes.Length == 0)
            throw new InvalidOperationException("Memory:ProtectedBlockTypes must have at least one type.");
    }
}

public class TodoOptions
{
    public const string Section = "Todo";
    public string[] ValidStatuses { get; set; } = [];
    public string DefaultStatus { get; set; } = string.Empty;
    public string DefaultPriority { get; set; } = string.Empty;
    public void Validate()
    {
        if (ValidStatuses.Length == 0)
            throw new InvalidOperationException("Todo:ValidStatuses must have at least one status.");
        if (string.IsNullOrWhiteSpace(DefaultStatus))
            throw new InvalidOperationException("Todo:DefaultStatus is not configured.");
        if (string.IsNullOrWhiteSpace(DefaultPriority))
            throw new InvalidOperationException("Todo:DefaultPriority is not configured.");
        if (!ValidStatuses.Contains(DefaultStatus))
            throw new InvalidOperationException($"Todo:DefaultStatus '{DefaultStatus}' is not in ValidStatuses.");
    }
}

public class AgentOptions
{
    public const string Section = "Agent";
    public int MaxToolIterations { get; set; }
    public string AgentName { get; set; } = "Glyphite.MainAgent";
    public bool PeekReasoning { get; set; } = true;
    public bool PeekToolReasoning { get; set; } = false;
    public void Validate()
    {
        if (MaxToolIterations <= 0)
            throw new InvalidOperationException("Agent:MaxToolIterations must be > 0.");
        if (string.IsNullOrWhiteSpace(AgentName))
            throw new InvalidOperationException("Agent:AgentName is not configured.");
    }
}

public class SearchOptions
{
    public const string Section = "Search";
    public string[] ExcludedDirectories { get; set; } = [];
    public string[] BinaryExtensions { get; set; } = [];
    public int MaxResultCount { get; set; }
    public int MaxTextFileSize { get; set; }
    public int MaxLineLength { get; set; }
    public int DetectBinarySampleSize { get; set; }
    public int MaxEnumerationFiles { get; set; } = 50000;
    /// <summary>Max output characters for read_file before returning a size-hint error.</summary>
    public int MaxReadChars { get; set; } = 100_000;
    public void Validate()
    {
        if (MaxResultCount <= 0)
            throw new InvalidOperationException("Search:MaxResultCount must be > 0.");
        if (MaxTextFileSize <= 0)
            throw new InvalidOperationException("Search:MaxTextFileSize must be > 0.");
        if (MaxLineLength <= 0)
            throw new InvalidOperationException("Search:MaxLineLength must be > 0.");
        if (DetectBinarySampleSize <= 0)
            throw new InvalidOperationException("Search:DetectBinarySampleSize must be > 0.");
        if (MaxEnumerationFiles <= 0)
            throw new InvalidOperationException("Search:MaxEnumerationFiles must be > 0.");
        if (MaxReadChars <= 0)
            throw new InvalidOperationException("Search:MaxReadChars must be > 0.");
    }
}

public class DataOptions
{
    public const string Section = "Data";
    public string Directory { get; set; } = string.Empty;
    public string DatabaseFileName { get; set; } = string.Empty;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Directory))
            throw new InvalidOperationException("Data:Directory is not configured.");
        if (string.IsNullOrWhiteSpace(DatabaseFileName))
            throw new InvalidOperationException("Data:DatabaseFileName is not configured.");
    }
}

public class ToolStreamingOptions
{
    public const string Section = "ToolStreaming";
    public Dictionary<string, int> ToolMaxLength { get; set; } = [];
    public Dictionary<string, string[]> ToolHiddenArgs { get; set; } = [];

    /// <summary>
    /// Lookup max length for a tool name. Supports two kinds of keys:
    /// <list type="bullet">
    ///   <item><b>Exact match</b> — key equals the tool name (e.g. <c>"codegraph_search"</c>). Highest priority.</item>
    ///   <item><b>Prefix match</b> — key is a prefix of the tool name (e.g. <c>"codegraph_"</c> matches any tool starting with it).
    ///         If multiple prefixes match, the <b>longest</b> one wins.</item>
    /// </list>
    /// Exact match always beats any prefix match. Returns <paramref name="defaultValue"/> when nothing matches.
    /// </summary>
    public int GetMaxLength(string toolName, int defaultValue = -1)
    {
        // 1. Exact match — highest priority
        if (ToolMaxLength.TryGetValue(toolName, out var exact))
            return exact;

        // 2. Prefix match — longest matching prefix wins
        var best = defaultValue;
        var longest = -1;
        foreach (var (key, value) in ToolMaxLength)
        {
            if (toolName.StartsWith(key, StringComparison.Ordinal) && key.Length > longest)
            {
                longest = key.Length;
                best = value;
            }
        }
        return best;
    }
}

public class CompressionOptions
{
    public const string Section = "Compression";
    public int AutoThreshold { get; set; }
    public bool AutoCompress { get; set; }
    /// <summary>Known strategy names.</summary>
    internal static readonly string[] KnownStrategies = ["fibo-parts", "struct-cut"];

    /// <summary>Strategy flags. At least one must be enabled. If multiple are enabled, one is picked randomly per compaction cycle.</summary>
    public Dictionary<string, bool> Strategies { get; set; } = new() { ["fibo-parts"] = true };
    public int CacheHitRateThreshold { get; set; } = 80;
    public double CostSignificantThreshold { get; set; } = 0.01;
    public void Validate()
    {
        if (AutoThreshold < 0 || AutoThreshold > 100)
            throw new InvalidOperationException("Compression:AutoThreshold must be between 0 and 100.");
        if (Strategies is null || Strategies.Count == 0 || !Strategies.Values.Any(v => v))
            throw new InvalidOperationException("Compression:Strategies must have at least one enabled strategy.");
        foreach (var key in Strategies.Keys)
        {
            if (!KnownStrategies.Contains(key))
                throw new InvalidOperationException($"Compression:Strategies contains unknown strategy '{key}'. Known: {string.Join(", ", KnownStrategies)}.");
        }
        if (CacheHitRateThreshold < 0 || CacheHitRateThreshold > 100)
            throw new InvalidOperationException("Compression:CacheHitRateThreshold must be between 0 and 100.");
        if (CostSignificantThreshold < 0)
            throw new InvalidOperationException("Compression:CostSignificantThreshold must be non-negative.");
    }
}