using System.Collections.Concurrent;
using System.Reflection;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Services;

/// <summary>
/// Builds merged system instructions for any agent:
/// system-prompt.md (embedded, always) + AGENTS.md (optional) + Glyphite.{agentId}.md (optional).
/// AGENTS.md and Glyphite.{agentId}.md are read in cascade (home → parentCwd → agentCwd, top wins).
/// Cached in memory — re-read on each turn only if TurnReload* flags are true.
/// Instructions are NOT persisted to DB.
/// </summary>
public class InstructionProvider : IInstructionProvider
{
    private static string? _systemPromptCache;
    private static readonly object _systemPromptLock = new();

    private readonly ConcurrentDictionary<string, CachedFile> _agentsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedFile> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IConfigService _cfgService;
    private readonly ILogger _logger;

    public InstructionProvider(IConfigService cfgService, ILogger<InstructionProvider>? logger = null)
    {
        _cfgService = cfgService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InstructionProvider>.Instance;
    }

    /// <summary>
    /// Build merged instructions string for the given agent.
    /// </summary>
    public async Task<string> BuildInstructionsAsync(string agentId, string? homePath, string parentCwd, string agentCwd)
    {
        // 1. system-prompt.md (embedded — always, cached forever)
        var basePrompt = GetSystemPrompt();
        var parts = new List<string> { basePrompt };

        // 2. AGENTS.md (if ReadAgentsFile)
        var memOpts = await _cfgService.GetOptionsAsync<MemoryOptions>(MemoryOptions.Section, agentId);
        if (memOpts.ReadAgentsFile)
        {
            var agentsMd = await ReadCascadeAsync(agentId, "AGENTS.md", homePath, parentCwd, agentCwd,
                _agentsCache, memOpts.TurnReloadAgentsFile);
            if (agentsMd is not null)
                parts.Add(agentsMd);
        }

        // 3. Glyphite.{agentId}.md (always, if exists)
        var nameMd = await ReadCascadeAsync(agentId, $"Glyphite.{agentId}.md", homePath, parentCwd, agentCwd,
            _nameCache, memOpts.TurnReloadNameFile);
        if (nameMd is not null)
            parts.Add(nameMd);

        return string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>Read a file in cascade (home → parentCwd → agentCwd), top wins. Returns null if not found.</summary>
    private async Task<string?> ReadCascadeAsync(
        string agentId, string fileName, string? homePath, string parentCwd, string agentCwd,
        ConcurrentDictionary<string, CachedFile> cache, bool turnReload)
    {
        var cacheKey = $"{agentId}:{fileName}";

        // Check cache
        if (cache.TryGetValue(cacheKey, out var cached) && !turnReload)
            return cached.Content;

        // Cascade read
        string? content = null;
        var paths = new List<string>();
        if (homePath is not null) paths.Add(homePath);
        paths.Add(parentCwd);
        if (!string.Equals(agentCwd, parentCwd, StringComparison.OrdinalIgnoreCase))
            paths.Add(agentCwd);

        foreach (var dir in paths)
        {
            var filePath = Path.Combine(dir, fileName);
            try
            {
                if (File.Exists(filePath))
                    content = await File.ReadAllTextAsync(filePath);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to read instruction file '{Path}' for agent '{AgentId}'", filePath, agentId);
            }
        }

        _logger.LogDebug("Agent '{AgentId}': loaded '{FileName}' from '{Dir}' (cached: {Cached})",
            agentId, fileName, paths.FirstOrDefault(p => File.Exists(Path.Combine(p, fileName))) ?? "(not found)", turnReload ? "no" : "yes");

        cache[cacheKey] = new CachedFile(content);
        return content;
    }

    private static string GetSystemPrompt()
    {
        if (_systemPromptCache is not null)
            return _systemPromptCache;

        lock (_systemPromptLock)
        {
            if (_systemPromptCache is not null)
                return _systemPromptCache;

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("Glyphite.Host.system-prompt.md");
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                _systemPromptCache = reader.ReadToEnd().Trim();
            }
            else
            {
                _systemPromptCache = "You are Glyphite, an expert software engineer and coding agent. You are precise, thorough, and efficient.";
            }

            return _systemPromptCache;
        }
    }

    public void InvalidateCache(string agentId)
    {
        // Remove all entries for this agent
        var keysToRemove = _agentsCache.Keys.Where(k => k.StartsWith(agentId + ":", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var key in keysToRemove)
            _agentsCache.TryRemove(key, out _);

        var nameKeysToRemove = _nameCache.Keys.Where(k => k.StartsWith(agentId + ":", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var key in nameKeysToRemove)
            _nameCache.TryRemove(key, out _);

        _logger.LogDebug("Invalidated instruction cache for agent '{AgentId}'", agentId);
    }

    private record CachedFile(string? Content);
}
