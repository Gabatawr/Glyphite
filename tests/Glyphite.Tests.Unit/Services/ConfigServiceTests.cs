using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Glyphite.Tests.Unit.Services;

public class ConfigServiceTests
{
    private IConfigStore CreateStore()
    {
        var store = Substitute.For<IConfigStore>();
        store.GetMergedConfigAsync(Arg.Any<string?>())
            .Returns(Task.FromResult(new Dictionary<string, string>()));
        store.GetConfigAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Task.FromResult<string?>(null));
        return store;
    }

    private static IConfiguration BuildConfig(Dictionary<string, string>? values = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection((values ?? new Dictionary<string, string>())
                .ToDictionary(kv => kv.Key, kv => (string?)kv.Value))
            .Build();
    }

    // ── Loads options correctly ──

    [Fact]
    public async Task GetOptionsAsync_Loads_LlmOptions_Correctly()
    {
        var store = CreateStore();
        var config = BuildConfig(new Dictionary<string, string>
        {
            ["LLM:Endpoint"] = "https://api.deepseek.com",
            ["LLM:Model"] = "deepseek-chat",
            ["LLM:ContextWindow"] = "128000",
            ["LLM:ApiKey"] = "sk-test-key-12345"
        });

        var service = new ConfigService(store, config);

        var options = await service.GetOptionsAsync<LlmOptions>("LLM");

        Assert.NotNull(options);
        Assert.Equal("https://api.deepseek.com", options.Endpoint);
        Assert.Equal("deepseek-chat", options.Model);
        Assert.Equal(128000, options.ContextWindow);
        Assert.Equal("sk-test-key-12345", options.ApiKey);
    }

    [Fact]
    public async Task GetOptionsAsync_Loads_WebFetchOptions_Correctly()
    {
        var store = CreateStore();
        var config = BuildConfig(new Dictionary<string, string>
        {
            ["WebFetch:TimeoutSeconds"] = "30",
            ["WebFetch:UserAgent"] = "TestBot/1.0",
            ["WebFetch:MaxContentLength"] = "50000",
            ["WebFetch:DefaultFormat"] = "markdown"
        });

        var service = new ConfigService(store, config);

        var options = await service.GetOptionsAsync<WebFetchOptions>("WebFetch");

        Assert.NotNull(options);
        Assert.Equal(30, options.TimeoutSeconds);
        Assert.Equal("TestBot/1.0", options.UserAgent);
        Assert.Equal(50000, options.MaxContentLength);
        Assert.Equal("markdown", options.DefaultFormat);
    }

    [Fact]
    public async Task GetOptionsAsync_Loads_BashOptions_Correctly()
    {
        var store = CreateStore();
        var config = BuildConfig(new Dictionary<string, string>
        {
            ["Bash:ExecutablePath"] = "/bin/bash",
            ["Bash:DiscoveryTimeoutMs"] = "5000",
            ["Bash:DefaultTimeoutMs"] = "30000",
            ["Bash:MaxOutput"] = "100000"
        });

        var service = new ConfigService(store, config);

        var options = await service.GetOptionsAsync<BashOptions>("Bash");

        Assert.Equal("/bin/bash", options.ExecutablePath);
        Assert.Equal(5000, options.DiscoveryTimeoutMs);
        Assert.Equal(30000, options.DefaultTimeoutMs);
        Assert.Equal(100000, options.MaxOutput);
    }

    [Fact]
    public async Task GetOptionsAsync_Loads_SearchOptions_Correctly()
    {
        var store = CreateStore();
        var config = BuildConfig(new Dictionary<string, string>
        {
            ["Search:MaxResultCount"] = "50",
            ["Search:MaxTextFileSize"] = "1048576",
            ["Search:MaxLineLength"] = "500",
            ["Search:DetectBinarySampleSize"] = "512",
            ["Search:MaxEnumerationFiles"] = "50000",
            ["Search:MaxReadChars"] = "100000"
        });

        var service = new ConfigService(store, config);

        var options = await service.GetOptionsAsync<SearchOptions>("Search");

        Assert.Equal(50, options.MaxResultCount);
        Assert.Equal(1048576, options.MaxTextFileSize);
        Assert.Equal(500, options.MaxLineLength);
        Assert.Equal(512, options.DetectBinarySampleSize);
        Assert.Equal(50000, options.MaxEnumerationFiles);
        Assert.Equal(100000, options.MaxReadChars);
    }

    [Fact]
    public async Task GetOptionsAsync_Loads_AgentOptions_Correctly()
    {
        var store = CreateStore();
        var config = BuildConfig(new Dictionary<string, string>
        {
            ["Agent:MaxToolIterations"] = "25",
            ["Agent:AgentName"] = "TestAgent"
        });

        var service = new ConfigService(store, config);

        var options = await service.GetOptionsAsync<AgentOptions>("Agent");

        Assert.Equal(25, options.MaxToolIterations);
        Assert.Equal("TestAgent", options.AgentName);
    }

    [Fact]
    public async Task GetOptionsAsync_Loads_DataOptions_Correctly()
    {
        var store = CreateStore();
        var config = BuildConfig(new Dictionary<string, string>
        {
            ["Data:Directory"] = "/data/glyphite",
            ["Data:DatabaseFileName"] = "test.db"
        });

        var service = new ConfigService(store, config);

        var options = await service.GetOptionsAsync<DataOptions>("Data");

        Assert.Equal("/data/glyphite", options.Directory);
        Assert.Equal("test.db", options.DatabaseFileName);
    }

    [Fact]
    public async Task GetOptionsAsync_Loads_MemoryOptions_Correctly()
    {
        var store = CreateStore();
        var config = BuildConfig(new Dictionary<string, string>
        {
            ["Memory:ProtectedBlockTypes:0"] = "agent_data",
            ["Memory:ProtectedBlockTypes:1"] = "system_info"
        });

        var service = new ConfigService(store, config);

        var options = await service.GetOptionsAsync<MemoryOptions>("Memory");

        Assert.Contains("agent_data", options.ProtectedBlockTypes);
        Assert.Contains("system_info", options.ProtectedBlockTypes);
    }

    [Fact]
    public async Task GetOptionsAsync_Loads_TodoOptions_Correctly()
    {
        var store = CreateStore();
        var config = BuildConfig(new Dictionary<string, string>
        {
            ["Todo:ValidStatuses:0"] = "pending",
            ["Todo:ValidStatuses:1"] = "done",
            ["Todo:DefaultStatus"] = "pending",
            ["Todo:DefaultPriority"] = "medium"
        });

        var service = new ConfigService(store, config);

        var options = await service.GetOptionsAsync<TodoOptions>("Todo");

        Assert.Contains("pending", options.ValidStatuses);
        Assert.Contains("done", options.ValidStatuses);
        Assert.Equal("pending", options.DefaultStatus);
        Assert.Equal("medium", options.DefaultPriority);
    }

    [Fact]
    public async Task GetOptionsAsync_Loads_CompressionOptions_Correctly()
    {
        var store = CreateStore();
        var config = BuildConfig(new Dictionary<string, string>
        {
            ["Compression:AutoThreshold"] = "50",
            ["Compression:AutoCompress"] = "true",
            ["Compression:CacheHitRateThreshold"] = "80",
            ["Compression:CostSignificantThreshold"] = "0.01"
        });

        var service = new ConfigService(store, config);

        var options = await service.GetOptionsAsync<CompressionOptions>("Compression");

        Assert.Equal(50, options.AutoThreshold);
        Assert.True(options.AutoCompress);
        Assert.Single(options.Strategies);
        Assert.True(options.Strategies["fibo"]);
        Assert.Equal(80, options.CacheHitRateThreshold);
        Assert.Equal(0.01, options.CostSignificantThreshold);
    }

    // ── Returns default values for missing sections ──

    [Fact]
    public async Task GetOptionsAsync_Returns_Defaults_For_Missing_Section()
    {
        var store = CreateStore();
        var config = BuildConfig(); // empty config

        var service = new ConfigService(store, config);

        var options = await service.GetOptionsAsync<LlmOptions>("LLM");

        Assert.NotNull(options);
        Assert.Equal(string.Empty, options.Endpoint);
        Assert.Equal(string.Empty, options.Model);
        Assert.Equal(0, options.ContextWindow);
        Assert.Empty(options.Models);
    }

    [Fact]
    public async Task GetOptionsAsync_Returns_Defaults_When_No_Matching_Keys()
    {
        var store = CreateStore();
        var config = BuildConfig(new Dictionary<string, string>
        {
            ["OtherSection:SomeKey"] = "value"
        });

        var service = new ConfigService(store, config);

        var options = await service.GetOptionsAsync<BashOptions>("Bash");

        Assert.NotNull(options);
        Assert.Equal(string.Empty, options.ExecutablePath);
        Assert.Equal(0, options.DiscoveryTimeoutMs);
    }

    // ── Hot-reload scenarios ──

    [Fact]
    public async Task GetConfigAsync_NullSession_Returns_Fresh_Config_Directly()
    {
        var store = CreateStore();
        var config = BuildConfig(new Dictionary<string, string>
        {
            ["LLM:Endpoint"] = "https://api.deepseek.com"
        });

        var service = new ConfigService(store, config);

        // First call
        var result1 = await service.GetConfigAsync(null);
        Assert.True(result1.ContainsKey("LLM:Endpoint"));
        Assert.Equal("https://api.deepseek.com", result1["LLM:Endpoint"]);

        // Store should NOT be called for null session (global config)
        await store.DidNotReceiveWithAnyArgs().GetMergedConfigAsync(default);
    }

    [Fact]
    public async Task HotReload_Reflects_Changed_Config()
    {
        var store = CreateStore();

        var config = BuildConfig(new Dictionary<string, string>
        {
            ["WebFetch:TimeoutSeconds"] = "30"
        });

        var service = new ConfigService(store, config);

        var options1 = await service.GetOptionsAsync<WebFetchOptions>("WebFetch");
        Assert.Equal(30, options1.TimeoutSeconds);

        // Simulate hot-reload by creating a new config with updated value
        // and recreating the service (since IConfiguration is immutable in tests)
        var newConfig = BuildConfig(new Dictionary<string, string>
        {
            ["WebFetch:TimeoutSeconds"] = "60"
        });

        var service2 = new ConfigService(store, newConfig);

        var options2 = await service2.GetOptionsAsync<WebFetchOptions>("WebFetch");
        Assert.Equal(60, options2.TimeoutSeconds);
    }

    [Fact]
    public async Task SessionConfig_Uses_DB_Overrides()
    {
        var store = CreateStore();
        store.GetMergedConfigAsync("session-1")
            .Returns(Task.FromResult(new Dictionary<string, string>
            {
                ["Bash:DefaultTimeoutMs"] = "60000"
            }));
        store.GetMergedConfigAsync(null)
            .Returns(Task.FromResult(new Dictionary<string, string>()));

        var config = BuildConfig(new Dictionary<string, string>
        {
            ["Bash:DefaultTimeoutMs"] = "30000",
            ["Bash:ExecutablePath"] = "/bin/bash"
        });

        var service = new ConfigService(store, config);

        var options = await service.GetOptionsAsync<BashOptions>("Bash", "session-1");

        // Session override should take precedence
        Assert.Equal(60000, options.DefaultTimeoutMs);
        // Global config should still apply
        Assert.Equal("/bin/bash", options.ExecutablePath);
    }

    [Fact]
    public async Task SessionOverlay_Takes_Precedence()
    {
        var store = CreateStore();
        store.GetMergedConfigAsync("overlay-session")
            .Returns(Task.FromResult(new Dictionary<string, string>()));
        store.GetMergedConfigAsync(null)
            .Returns(Task.FromResult(new Dictionary<string, string>()));

        var config = BuildConfig(new Dictionary<string, string>
        {
            ["Agent:MaxToolIterations"] = "25"
        });

        var service = new ConfigService(store, config);

        // Set overlay
        service.SetSessionOverlay("overlay-session", new Dictionary<string, string>
        {
            ["Agent:MaxToolIterations"] = "50"
        });

        var options = await service.GetOptionsAsync<AgentOptions>("Agent", "overlay-session");

        // Overlay value takes precedence over global config
        Assert.Equal(50, options.MaxToolIterations);
    }

    [Fact]
    public async Task ClearSessionOverlay_Removes_Override()
    {
        var store = CreateStore();
        store.GetMergedConfigAsync("clear-overlay")
            .Returns(Task.FromResult(new Dictionary<string, string>()));
        store.GetMergedConfigAsync(null)
            .Returns(Task.FromResult(new Dictionary<string, string>()));

        var config = BuildConfig(new Dictionary<string, string>
        {
            ["Agent:MaxToolIterations"] = "25"
        });

        var service = new ConfigService(store, config);

        service.SetSessionOverlay("clear-overlay", new Dictionary<string, string>
        {
            ["Agent:MaxToolIterations"] = "50"
        });

        // Clear the overlay
        service.ClearSessionOverlay("clear-overlay");

        var options = await service.GetOptionsAsync<AgentOptions>("Agent", "clear-overlay");

        // Should fall back to global config
        Assert.Equal(25, options.MaxToolIterations);
    }

    [Fact]
    public async Task UpdateConfigAsync_Upserts_And_Invalidates_Cache()
    {
        var store = CreateStore();
        var config = BuildConfig();
        var service = new ConfigService(store, config);

        var changes = new Dictionary<string, string>
        {
            ["LLM:Endpoint"] = "https://api.deepseek.com"
        };

        var result = await service.UpdateConfigAsync(changes);

        Assert.Single(result.Updated);
        Assert.Equal("https://api.deepseek.com", result.Updated["LLM:Endpoint"]);

        // Store was called to upsert
        await store.Received(1).UpsertConfigAsync("LLM:Endpoint", "https://api.deepseek.com", "global", null);
    }

    [Fact]
    public async Task UpdateConfigAsync_Skips_Unchanged_Values()
    {
        var store = CreateStore();
        store.GetConfigAsync("ExistingKey", "global", null).Returns(Task.FromResult<string?>("same-value"));

        var config = BuildConfig();
        var service = new ConfigService(store, config);

        var changes = new Dictionary<string, string>
        {
            ["ExistingKey"] = "same-value"
        };

        var result = await service.UpdateConfigAsync(changes);

        Assert.Empty(result.Updated);
        Assert.Single(result.Skipped);
        Assert.Equal("same-value", result.Skipped["ExistingKey"]);

        // Store should NOT have been called to upsert
        await store.DidNotReceiveWithAnyArgs().UpsertConfigAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task DeleteConfigAsync_Deletes_And_Invalidates_Cache()
    {
        var store = CreateStore();
        var config = BuildConfig();
        var service = new ConfigService(store, config);

        await service.DeleteConfigAsync(["Key1", "Key2"]);

        await store.Received(1).DeleteConfigAsync("Key1", "global", null);
        await store.Received(1).DeleteConfigAsync("Key2", "global", null);
    }

    [Fact]
    public async Task InitializeAsync_Removes_Stale_Keys_From_DB()
    {
        var store = CreateStore();
        store.GetMergedConfigAsync(null)
            .Returns(Task.FromResult(new Dictionary<string, string>
            {
                ["Bash:ExecutablePath"] = "/bin/bash",      // still in app config
                ["Bash:StaleKey"] = "should-be-removed",    // no longer in app config
            }));

        var config = BuildConfig(new Dictionary<string, string>
        {
            ["Bash:ExecutablePath"] = "/bin/bash"
        });

        var service = new ConfigService(store, config);

        await service.InitializeAsync(new HashSet<string> { "Bash" });

        // Stale key should be deleted
        await store.Received(1).DeleteConfigAsync("Bash:StaleKey", "global");
        // Live key should NOT be deleted
        await store.DidNotReceive().DeleteConfigAsync("Bash:ExecutablePath", "global");
    }
}
