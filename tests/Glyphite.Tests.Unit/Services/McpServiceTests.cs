using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Glyphite.Host.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

public class McpServiceTests
{
    // ── ResolveExecution: hierarchical priority merge ──

    [Fact]
    public void ResolveExecution_NoEntries_UsesSharedDefaults()
    {
        var result = McpService.ResolveExecution("srv", "tool", []);

        Assert.Equal(ToolExecutionDefaults.McpTimeoutSeconds, result.Timeout);
        Assert.Equal(ToolExecutionDefaults.ContentMaxSize, result.MaxSize);
        Assert.Equal(ToolExecutionDefaults.Peek, result.Peek);
    }

    [Fact]
    public void ResolveExecution_WildcardServer_NoTool_Applies()
    {
        var result = McpService.ResolveExecution("any", "any", [Entry("*", null, timeout: 10)]);

        Assert.Equal(10, result.Timeout);
    }

    [Fact]
    public void ResolveExecution_ExactServer_OverridesWildcardServer()
    {
        var entries = new[]
        {
            Entry("*", null, timeout: 10),
            Entry("srv", null, timeout: 20),
        };

        var result = McpService.ResolveExecution("srv", "tool", entries);

        Assert.Equal(20, result.Timeout);
    }

    [Fact]
    public void ResolveExecution_ExactTool_OverridesServerLevel()
    {
        var entries = new[]
        {
            Entry("srv", "*", timeout: 10),
            Entry("srv", "tool", timeout: 30),
        };

        var result = McpService.ResolveExecution("srv", "tool", entries);

        Assert.Equal(30, result.Timeout);
    }

    [Fact]
    public void ResolveExecution_HighestPriority_Wins_AmongAllLevels()
    {
        var entries = new[]
        {
            Entry("*", null, timeout: 1, maxSize: 100),
            Entry("srv", null, timeout: 2, maxSize: 200),
            Entry("*", "*", timeout: 3, maxSize: 300),
            Entry("srv", "*", timeout: 4, maxSize: 400),
            Entry("*", "tool", timeout: 5, maxSize: 500),
            Entry("srv", "tool", timeout: 6, maxSize: 600),
        };

        var result = McpService.ResolveExecution("srv", "tool", entries);

        Assert.Equal(6, result.Timeout);
        Assert.Equal(600, result.MaxSize);
    }

    [Fact]
    public void ResolveExecution_PartialOverride_InheritsLowerPriority()
    {
        var entries = new[]
        {
            Entry("srv", null, timeout: 10, maxSize: 500),
            Entry("srv", "tool", peek: true), // only peek overridden
        };

        var result = McpService.ResolveExecution("srv", "tool", entries);

        Assert.Equal(10, result.Timeout);  // inherited from server level
        Assert.Equal(500, result.MaxSize); // inherited from server level
        Assert.True(result.Peek);          // overridden by exact tool
    }

    [Fact]
    public void ResolveExecution_NonMatchingEntries_Ignored()
    {
        var entries = new[]
        {
            Entry("other", null, timeout: 10),
            Entry("*", "othertool", timeout: 20),
            Entry("srv", "othertool", timeout: 30),
        };

        var result = McpService.ResolveExecution("srv", "tool", entries);

        Assert.Equal(ToolExecutionDefaults.McpTimeoutSeconds, result.Timeout);
    }

    [Fact]
    public void ResolveExecution_NullOptions_Skipped()
    {
        var entries = new[] { new McpExecutionEntry { Mcp = "srv", Tool = null } }; // Options = null

        var result = McpService.ResolveExecution("srv", "tool", entries);

        Assert.Equal(ToolExecutionDefaults.McpTimeoutSeconds, result.Timeout);
    }

    [Fact]
    public void ResolveExecution_CaseInsensitive_ServerAndTool()
    {
        var entries = new[]
        {
            Entry("SRV", "TOOL", timeout: 42),
        };

        var result = McpService.ResolveExecution("srv", "tool", entries);

        Assert.Equal(42, result.Timeout);
    }

    // ── GetToolsAsync / GetServersAsync with empty config (no network) ──

    [Fact]
    public async Task GetToolsAsync_NoServers_ReturnsEmpty()
    {
        var svc = CreateService(new McpServersConfig());

        var tools = await svc.GetToolsAsync("agent");

        Assert.Empty(tools);
    }

    [Fact]
    public async Task GetServersAsync_NoServers_ReturnsEmpty()
    {
        var svc = CreateService(new McpServersConfig());

        var servers = await svc.GetServersAsync("agent");

        Assert.Empty(servers);
    }

    [Fact]
    public async Task GetServersAsync_DisabledServer_ReportsDisabled_WithoutConnecting()
    {
        var svc = CreateService(new McpServersConfig
        {
            Servers = new Dictionary<string, McpServerOptions>
            {
                ["off"] = new() { Enabled = false, Type = "stdio", Command = "nonexistent-cmd" },
            },
        });

        var servers = await svc.GetServersAsync("agent");

        var info = Assert.Single(servers);
        Assert.Equal("off", info.Name);
        Assert.Equal(McpServerStatus.Disabled, info.Status);
        Assert.Equal(0, info.ToolCount);
        Assert.Null(info.Error);
    }

    private static McpService CreateService(McpServersConfig config)
    {
        var cfg = Substitute.For<IConfigService>();
        cfg.GetOptionsAsync<McpServersConfig>(McpServersConfig.Section, Arg.Any<string?>())
            .Returns(config);
        return new McpService(cfg, NullLogger<McpService>.Instance);
    }

    private static McpExecutionEntry Entry(string mcp, string? tool, int? timeout = null, int? maxSize = null, bool? peek = null)
        => new()
        {
            Mcp = mcp,
            Tool = tool,
            Options = new McpExecutionOptionsEntry { Timeout = timeout, MaxSize = maxSize, Peek = peek },
        };
}
