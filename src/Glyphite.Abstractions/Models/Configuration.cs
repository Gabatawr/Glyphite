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
        // ApiKey is NOT validated here — validated lazily at turn time.
        // This allows the app to start even without a key, show welcome,
        // and create a default Glyphite.json for the user to edit.
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
    public string UserAgent { get; set; } = string.Empty;
    public string DefaultFormat { get; set; } = string.Empty;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(UserAgent))
            throw new InvalidOperationException("WebFetch:UserAgent is not configured.");
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
    public string[] AllowedExecutables { get; set; } = [];
    public string[] ForbiddenCommands { get; set; } = [];
    public string[] ForbiddenDirectories { get; set; } = [];
    /// <summary>Commands requiring interactive confirmation (Check/OK/Stop). Timeout in seconds. 0 = no timeout.</summary>
    public string[] CheckRequireCommands { get; set; } = [];
    /// <summary>Timeout in seconds for interactive confirmation. Default 10s.</summary>
    public int CheckRequireCommandsTimeout { get; set; } = 10;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath))
            throw new InvalidOperationException("Bash:ExecutablePath is not configured.");
        if (DiscoveryTimeoutMs <= 0)
            throw new InvalidOperationException("Bash:DiscoveryTimeoutMs must be > 0.");
    }
}

public class MemoryOptions
{
    public const string Section = "Memory";
    /// <summary>If true, cascade-read AGENTS.md (home → parentCwd → agentCwd) and append to system prompt.</summary>
    public bool ReadAgentsFile { get; set; } = false;
    /// <summary>If true, re-read AGENTS.md from disk on every turn. If false, cache in memory.</summary>
    public bool TurnReloadAgentsFile { get; set; } = false;
    /// <summary>If true, re-read Glyphite.{agentId}.md from disk on every turn. If false, cache in memory.</summary>
    public bool TurnReloadNameFile { get; set; } = false;
    public void Validate() { }
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

/// <summary>Per‑tool streaming options entry (array format, mirrors ToolExecution).</summary>
public class ToolStreamingEntry
{
    public string Tool { get; set; } = "";
    public ToolStreamingOptionsEntry? Options { get; set; }
}

/// <summary>Streaming display options for a single tool.</summary>
public class ToolStreamingOptionsEntry
{
    /// <summary>Max result chars to show. 0 = hide, -1 = unlimited.</summary>
    public int MaxSize { get; set; } = -1;
    /// <summary>Arg names to mask with *** in the display.</summary>
    public string[] HiddenArgs { get; set; } = [];
}

public class ToolStreamingOptions
{
    public const string Section = "ToolStreaming";

    /// <summary>Raw entries from config binding.</summary>
    public ToolStreamingEntry[] Entries { get; set; } = [];

    // Built lazily from Entries
    private Dictionary<string, int>? _maxLengthLookup;
    private Dictionary<string, string[]>? _hiddenArgsLookup;

    private void EnsureLookup()
    {
        if (_maxLengthLookup is not null) return;
        _maxLengthLookup = new(StringComparer.OrdinalIgnoreCase);
        _hiddenArgsLookup = new(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Entries)
        {
            if (string.IsNullOrEmpty(entry.Tool)) continue;
            var opts = entry.Options ?? new ToolStreamingOptionsEntry();
            // First entry for a tool wins — this ensures user overrides in
            // Glyphite.json (loaded last, lower indices) take priority over
            // appsettings.json defaults (higher indices) when arrays merge
            // by index instead of being replaced entirely.
            _maxLengthLookup.TryAdd(entry.Tool, opts.MaxSize);
            if (!_hiddenArgsLookup.ContainsKey(entry.Tool))
                _hiddenArgsLookup[entry.Tool] = opts.HiddenArgs ?? [];
        }
    }

    /// <summary>
    /// Lookup max length for a tool name. Key matching rules:
    /// <list type="bullet">
    ///   <item><b>Exact match</b> — key equals the tool name. Highest priority.</item>
    ///   <item><b>Wildcard match</b> — key ends with <c>*</c> (e.g. <c>"codegraph_*"</c>) → matches any tool
    ///         whose name starts with the prefix (without the trailing <c>*</c>).</item>
    /// </list>
    /// Exact match always beats any wildcard match.
    /// If multiple wildcard keys match, the <b>longest</b> prefix wins.
    /// Returns <paramref name="defaultValue"/> when nothing matches.
    /// </summary>
    public int GetMaxLength(string toolName, int defaultValue = -1)
    {
        EnsureLookup();

        // 1. Exact match — highest priority
        if (_maxLengthLookup!.TryGetValue(toolName, out var exact))
            return exact;

        // 2. Wildcard (*) match — longest matching prefix wins
        var best = defaultValue;
        var longest = -1;
        foreach (var (key, value) in _maxLengthLookup)
        {
            if (!key.EndsWith('*'))
                continue;
            var prefix = key[..^1];
            if (prefix.Length > longest && toolName.StartsWith(prefix, StringComparison.Ordinal))
            {
                longest = prefix.Length;
                best = value;
            }
        }
        return best;
    }

    /// <summary>
    /// Get hidden args for a tool name (empty array if none). Key matching rules:
    /// <list type="bullet">
    ///   <item><b>Exact match</b> — key equals the tool name. Highest priority.</item>
    ///   <item><b>Wildcard match</b> — key ends with <c>*</c> (e.g. <c>"codegraph_*"</c>) → matches any tool
    ///         whose name starts with the prefix (without the trailing <c>*</c>).</item>
    /// </list>
    /// Exact match always beats any wildcard match.
    /// If multiple wildcard keys match, the <b>longest</b> prefix wins.
    /// </summary>
    public string[] GetHiddenArgs(string toolName)
    {
        EnsureLookup();

        // 1. Exact match — highest priority
        if (_hiddenArgsLookup!.TryGetValue(toolName, out var exact))
            return exact;

        // 2. Wildcard (*) match — longest matching prefix wins
        string[]? best = null;
        var longest = -1;
        foreach (var (key, value) in _hiddenArgsLookup)
        {
            if (!key.EndsWith('*'))
                continue;
            var prefix = key[..^1];
            if (prefix.Length > longest && toolName.StartsWith(prefix, StringComparison.Ordinal))
            {
                longest = prefix.Length;
                best = value;
            }
        }
        return best ?? [];
    }
}

public class CompressionOptions
{
    public const string Section = "Compression";
    public int AutoThreshold { get; set; }
    public bool AutoCompress { get; set; }
    /// <summary>Known strategy names.</summary>
    internal static readonly string[] KnownStrategies = ["fibo", "struct"];

    /// <summary>Strategy flags. At least one must be enabled. If multiple are enabled, one is picked randomly per compaction cycle.</summary>
    public Dictionary<string, bool> Strategies { get; set; } = new() { ["fibo"] = true };
    /// <summary>Auto-compact large reasoning blocks via LLM after each turn.</summary>
    public bool AutoCompressReasoning { get; set; } = true;
    /// <summary>Reasoning blocks larger than this (in chars) will be LLM-compacted down to ~maxSize/4/2 tokens.</summary>
    public int AutoCompressReasoningMaxSize { get; set; } = 6_000;
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

/// <summary>Per‑tool execution settings from the ToolExecution config section.</summary>
public class ToolExecutionEntry
{
    public string Tool { get; set; } = "";
    public ToolExecutionOptionsEntry? Options { get; set; }
}

public class ToolExecutionOptionsEntry
{
    /// <summary>Timeout in seconds. 0 = use default.</summary>
    public int Timeout { get; set; } = 120;
    /// <summary>Max result size in characters. 0 = unlimited.</summary>
    public int MaxSize { get; set; } = 100_000;
    /// <summary>Auto‑clean result after tool loop (peek).</summary>
    public bool Peek { get; set; } = false;
}

/// <summary>
/// Image support (<c>view_image</c> tool + attachment of image paths/URLs found in user messages).
/// Defaults follow the provider's documented limits, so the agent stays inside them unless the
/// user deliberately widens them.
/// </summary>
public class ImageOptions
{
    public const string Section = "Image";

    /// <summary>Master switch — when false, <c>view_image</c> refuses and nothing is auto-attached.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Scan user messages for local image paths / image URLs and attach them automatically.</summary>
    public bool AutoAttach { get; set; } = true;

    /// <summary>
    /// How an <c>http(s)</c> image reaches the model.
    /// <c>auto</c> (default) is adaptive — a public URL is handed to the provider to download
    /// (no bytes through the agent), while a host the provider can never reach (localhost, private
    /// ranges, intranet names) is downloaded here and inlined.
    /// <c>inline</c> always downloads and sends the bytes itself.
    /// URLs past <see cref="MaxUrlLength"/> are inlined regardless, because the provider would reject them.
    /// Matched case-insensitively.
    /// </summary>
    public string UrlMode { get; set; } = "auto";

    /// <summary>What a URL must look like to be auto-attached: <c>extension</c> (default), <c>always</c>, <c>never</c>.</summary>
    public string UrlMatching { get; set; } = "extension";

    /// <summary>Detail level sent to the provider: <c>auto</c> (field omitted), <c>low</c>, <c>high</c>, <c>original</c>.</summary>
    public string Detail { get; set; } = "auto";

    /// <summary>Max size of a single inlined image. Provider limit: 32 MiB.</summary>
    public long MaxImageBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>Max total inlined bytes per request. Provider request-body limit: 48 MiB.</summary>
    public long MaxTotalBytes { get; set; } = 40L * 1024 * 1024;

    /// <summary>Max images attached to one request. Provider limit: 600.</summary>
    public int MaxImagesPerRequest { get; set; } = 10;

    /// <summary>Longest URL accepted for pass-through. Provider limit: 8192 characters.</summary>
    public int MaxUrlLength { get; set; } = 8192;

    /// <summary>Max pixels per image side. Provider limit: 8192 (drops to 4096 with 15+ images).</summary>
    public int MaxDimension { get; set; } = 8192;

    /// <summary>Deadline for downloading a remote image that must be inlined.</summary>
    public int DownloadTimeoutSeconds { get; set; } = 60;

    /// <summary>Extensions treated as images when scanning message text for attachments.</summary>
    public string[] Extensions { get; set; } = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    public static readonly string[] KnownUrlModes = ["auto", "inline"];
    public static readonly string[] KnownUrlMatching = ["extension", "always", "never"];
    public static readonly string[] KnownDetails = ["auto", "low", "high", "original"];

    public void Validate()
    {
        if (!KnownUrlModes.Contains(UrlMode, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Image:UrlMode must be one of: {string.Join(", ", KnownUrlModes)}.");
        if (!KnownUrlMatching.Contains(UrlMatching, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Image:UrlMatching must be one of: {string.Join(", ", KnownUrlMatching)}.");
        if (!KnownDetails.Contains(Detail, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Image:Detail must be one of: {string.Join(", ", KnownDetails)}.");
        if (MaxImageBytes <= 0)
            throw new InvalidOperationException("Image:MaxImageBytes must be > 0.");
        if (MaxTotalBytes <= 0)
            throw new InvalidOperationException("Image:MaxTotalBytes must be > 0.");
        if (MaxImagesPerRequest <= 0)
            throw new InvalidOperationException("Image:MaxImagesPerRequest must be > 0.");
        if (MaxUrlLength <= 0)
            throw new InvalidOperationException("Image:MaxUrlLength must be > 0.");
        if (MaxDimension <= 0)
            throw new InvalidOperationException("Image:MaxDimension must be > 0.");
        if (DownloadTimeoutSeconds <= 0)
            throw new InvalidOperationException("Image:DownloadTimeoutSeconds must be > 0.");
        if (Extensions.Length == 0)
            throw new InvalidOperationException("Image:Extensions must not be empty.");
        foreach (var ext in Extensions)
            if (!ext.StartsWith('.'))
                throw new InvalidOperationException($"Image:Extensions entries must start with a dot (got '{ext}').");
    }
}

