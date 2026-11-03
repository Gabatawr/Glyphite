using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.DI;
using Glyphite.Host.Images;
using Glyphite.Host.Services;
using Glyphite.Host.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

/// <summary>
/// The tools a turn hands the model must resolve relative paths against the agent's own directory
/// (a subagent's <c>cwd</c>), not the directory the process happens to run in. Verified on real runs
/// before the fix: <c>subagent_run(cwd: "/other/project")</c> issued <c>write_file("src/x.cs")</c> and
/// the file landed next to glyphite's own binary directory.
/// </summary>
public class ToolRegistryTests : IDisposable
{
    private const string AgentId = "test-agent";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "GlyphiteToolRegistry_" + Guid.NewGuid().ToString("N"));

    public ToolRegistryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task RelativeWrite_LandsInTheAgentsBaseDirectory()
    {
        // Unique name: the negative half of this test must not trip over a file another test left
        // behind in the working directory.
        var relative = Path.Combine("nested", "probe_" + Guid.NewGuid().ToString("N") + ".txt");
        var tools = await GetToolsAsync();

        await InvokeAsync(tools, "write_file", new()
        {
            ["path"] = relative,
            ["content"] = "hello",
        });

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, relative)));
        // ...and nothing was created relative to the process, which is where it used to go.
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), relative)));
    }

    [Fact]
    public async Task RelativeRead_ResolvesAgainstTheAgentsBaseDirectory()
    {
        var tools = await GetToolsAsync();

        // The error names the resolved path — that is the observable half of the fix.
        var result = await InvokeAsync(tools, "read_file", new() { ["path"] = "missing.txt" });

        Assert.Contains(Path.Combine(_root, "missing.txt"), result);
    }

    [Fact]
    public async Task RelativeSearch_FindsFilesUnderTheAgentsBaseDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "a.cs"), "// x");
        var tools = await GetToolsAsync();

        var result = await InvokeAsync(tools, "search_glob", new() { ["pattern"] = "**/*.cs" });

        Assert.Contains("a.cs", result);
    }

    [Fact]
    public async Task NoBaseDir_KeepsTheProcessDefault()
    {
        var tools = await GetToolsAsync(baseDir: null);

        // Same observable as above, but with no override: the process directory still wins.
        var result = await InvokeAsync(tools, "read_file", new() { ["path"] = "missing.txt" });

        Assert.Contains(Path.Combine(Directory.GetCurrentDirectory(), "missing.txt"), result);
    }

    private Task<IReadOnlyList<AITool>> GetToolsAsync(string? baseDir = "auto")
        => CreateRegistry().GetBuiltinToolsAsync(
            AgentId, includeMemory: false, baseDir: baseDir == "auto" ? _root : baseDir);

    private static async Task<string> InvokeAsync(IReadOnlyList<AITool> tools, string name, AIFunctionArguments args)
    {
        var tool = tools.OfType<AIFunction>().Single(t => t.Name == name);
        var result = await tool.InvokeAsync(args);
        return result?.ToString() ?? "";
    }

    private ToolRegistry CreateRegistry()
    {
        // Real ConfigService over an in-memory configuration: every section falls back to its
        // defaults, which is enough for tool wiring (Search needs its limits non-zero to enumerate).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Glyphite:Search:MaxResultCount"] = "50",
                ["Glyphite:Search:MaxTextFileSize"] = "1048576",
                ["Glyphite:Search:MaxLineLength"] = "500",
                ["Glyphite:Search:DetectBinarySampleSize"] = "512",
                ["Glyphite:Search:MaxEnumerationFiles"] = "50000",
            })
            .Build();

        var cfgStore = Substitute.For<IConfigStore>();
        cfgStore.GetMergedConfigAsync(Arg.Any<string?>())
            .Returns(Task.FromResult(new Dictionary<string, string>()));
        cfgStore.GetSessionConfigAsync(Arg.Any<string>())
            .Returns(Task.FromResult(new Dictionary<string, string>()));
        cfgStore.GetConfigAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Task.FromResult<string?>(null));
        var cfg = new ConfigService(cfgStore, configuration.GetSection("Glyphite"));

        var kvStore = Substitute.For<IKVStore>();
        kvStore.ListAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Task.FromResult(new Dictionary<string, string>()));

        return new ToolRegistry(
            Substitute.For<IBashSessionManager>(),
            cfg,
            Substitute.For<IAgentStore>(),
            Substitute.For<IBlockStore>(),
            Substitute.For<IBlockMemoryProvider>(),
            kvStore,
            new SubAgentManager(NullLogger<SubAgentManager>.Instance),
            Substitute.For<IAgentManager>(),
            Substitute.For<IAgentScopeFactory>(),
            new McpService(cfg, NullLogger<McpService>.Instance),
            new ImageLoader(),
            new ImageAttachmentSink(),
            configuration,
            NullLogger<ToolRegistry>.Instance,
            Substitute.For<ISafetyChecker>());
    }
}
