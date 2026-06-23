using Glyphite.Abstractions.Models;
using Xunit;

namespace Glyphite.Tests.Unit.Models;

public class ConfigurationValidationTests
{
    // ── LlmOptions ──

    [Fact]
    public void LlmOptions_Valid_DoesNotThrow()
    {
        var opts = new LlmOptions
        {
            Endpoint = "https://api.deepseek.com",
            Model = "deepseek-chat",
            ContextWindow = 128000,
            Models = [new LlmModel { Name = "deepseek-chat", Hit = 0.5, Miss = 0.5, Output = 1.0 }],
            ApiKey = "sk-test-key"
        };
        // Should not throw
        opts.Validate();
    }

    [Fact]
    public void LlmOptions_MissingEndpoint_Throws()
    {
        var opts = new LlmOptions
        {
            Endpoint = "",
            Model = "deepseek-chat",
            ContextWindow = 128000,
            Models = [new LlmModel()],
            ApiKey = "sk-test-key"
        };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("Endpoint", ex.Message);
    }

    [Fact]
    public void LlmOptions_MissingModel_Throws()
    {
        var opts = new LlmOptions
        {
            Endpoint = "https://api.deepseek.com",
            Model = "",
            ContextWindow = 128000,
            Models = [new LlmModel()],
            ApiKey = "sk-test-key"
        };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("Model", ex.Message);
    }

    [Fact]
    public void LlmOptions_ContextWindowZero_Throws()
    {
        var opts = new LlmOptions
        {
            Endpoint = "https://api.deepseek.com",
            Model = "deepseek-chat",
            ContextWindow = 0,
            Models = [new LlmModel()],
            ApiKey = "sk-test-key"
        };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("ContextWindow", ex.Message);
    }

    [Fact]
    public void LlmOptions_ContextWindowNegative_Throws()
    {
        var opts = new LlmOptions
        {
            Endpoint = "https://api.deepseek.com",
            Model = "deepseek-chat",
            ContextWindow = -1,
            Models = [new LlmModel()],
            ApiKey = "sk-test-key"
        };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("ContextWindow", ex.Message);
    }

    [Fact]
    public void LlmOptions_EmptyModels_Throws()
    {
        var opts = new LlmOptions
        {
            Endpoint = "https://api.deepseek.com",
            Model = "deepseek-chat",
            ContextWindow = 128000,
            Models = [],
            ApiKey = "sk-test-key"
        };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("Models", ex.Message);
    }

    [Fact]
    public void LlmOptions_MissingApiKey_Throws()
    {
        var opts = new LlmOptions
        {
            Endpoint = "https://api.deepseek.com",
            Model = "deepseek-chat",
            ContextWindow = 128000,
            Models = [new LlmModel()],
            ApiKey = ""
        };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("API key", ex.Message);
    }

    // ── WebFetchOptions ──

    [Fact]
    public void WebFetchOptions_Valid_DoesNotThrow()
    {
        var opts = new WebFetchOptions
        {
            TimeoutSeconds = 30,
            UserAgent = "TestAgent/1.0",
            MaxContentLength = 50000,
            DefaultFormat = "markdown"
        };
        opts.Validate();
    }

    [Fact]
    public void WebFetchOptions_TimeoutSecondsZero_Throws()
    {
        var opts = new WebFetchOptions
        {
            TimeoutSeconds = 0,
            UserAgent = "TestAgent/1.0",
            MaxContentLength = 50000,
            DefaultFormat = "markdown"
        };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("TimeoutSeconds", ex.Message);
    }

    [Fact]
    public void WebFetchOptions_TimeoutSecondsNegative_Throws()
    {
        var opts = new WebFetchOptions { TimeoutSeconds = -1, UserAgent = "a", MaxContentLength = 1, DefaultFormat = "a" };
        Assert.Throws<InvalidOperationException>(() => opts.Validate());
    }

    [Fact]
    public void WebFetchOptions_EmptyUserAgent_Throws()
    {
        var opts = new WebFetchOptions { TimeoutSeconds = 30, UserAgent = "", MaxContentLength = 50000, DefaultFormat = "markdown" };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("UserAgent", ex.Message);
    }

    [Fact]
    public void WebFetchOptions_MaxContentLengthZero_Throws()
    {
        var opts = new WebFetchOptions { TimeoutSeconds = 30, UserAgent = "a", MaxContentLength = 0, DefaultFormat = "markdown" };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("MaxContentLength", ex.Message);
    }

    [Fact]
    public void WebFetchOptions_MaxContentLengthNegative_Throws()
    {
        var opts = new WebFetchOptions { TimeoutSeconds = 30, UserAgent = "a", MaxContentLength = -1, DefaultFormat = "markdown" };
        Assert.Throws<InvalidOperationException>(() => opts.Validate());
    }

    [Fact]
    public void WebFetchOptions_EmptyDefaultFormat_Throws()
    {
        var opts = new WebFetchOptions { TimeoutSeconds = 30, UserAgent = "a", MaxContentLength = 50000, DefaultFormat = "" };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("DefaultFormat", ex.Message);
    }

    // ── ContentDedupOptions ──

    [Fact]
    public void ContentDedupOptions_Valid_DoesNotThrow()
    {
        var opts = new ContentDedupOptions
        {
            MinLines = 3,
            FrequencyThreshold = 0.5,
            MinLineLength = 10,
            MaxAliases = 5
        };
        opts.Validate();
    }

    [Fact]
    public void ContentDedupOptions_MinLinesZero_Throws()
    {
        var opts = new ContentDedupOptions { MinLines = 0, FrequencyThreshold = 0.5, MinLineLength = 10, MaxAliases = 5 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("MinLines", ex.Message);
    }

    [Fact]
    public void ContentDedupOptions_MinLinesNegative_Throws()
    {
        var opts = new ContentDedupOptions { MinLines = -1, FrequencyThreshold = 0.5, MinLineLength = 10, MaxAliases = 5 };
        Assert.Throws<InvalidOperationException>(() => opts.Validate());
    }

    [Fact]
    public void ContentDedupOptions_FrequencyThresholdZero_Throws()
    {
        var opts = new ContentDedupOptions { MinLines = 3, FrequencyThreshold = 0, MinLineLength = 10, MaxAliases = 5 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("FrequencyThreshold", ex.Message);
    }

    [Fact]
    public void ContentDedupOptions_FrequencyThresholdNegative_Throws()
    {
        var opts = new ContentDedupOptions { MinLines = 3, FrequencyThreshold = -0.1, MinLineLength = 10, MaxAliases = 5 };
        Assert.Throws<InvalidOperationException>(() => opts.Validate());
    }

    [Fact]
    public void ContentDedupOptions_FrequencyThresholdOverOne_Throws()
    {
        var opts = new ContentDedupOptions { MinLines = 3, FrequencyThreshold = 1.1, MinLineLength = 10, MaxAliases = 5 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("FrequencyThreshold", ex.Message);
    }

    [Fact]
    public void ContentDedupOptions_FrequencyThresholdAtOne_DoesNotThrow()
    {
        var opts = new ContentDedupOptions { MinLines = 3, FrequencyThreshold = 1.0, MinLineLength = 10, MaxAliases = 5 };
        opts.Validate();
    }

    [Fact]
    public void ContentDedupOptions_MinLineLengthZero_Throws()
    {
        var opts = new ContentDedupOptions { MinLines = 3, FrequencyThreshold = 0.5, MinLineLength = 0, MaxAliases = 5 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("MinLineLength", ex.Message);
    }

    [Fact]
    public void ContentDedupOptions_MaxAliasesZero_Throws()
    {
        var opts = new ContentDedupOptions { MinLines = 3, FrequencyThreshold = 0.5, MinLineLength = 10, MaxAliases = 0 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("MaxAliases", ex.Message);
    }

    // ── BashOptions ──

    [Fact]
    public void BashOptions_Valid_DoesNotThrow()
    {
        var opts = new BashOptions
        {
            ExecutablePath = "/bin/bash",
            DiscoveryTimeoutMs = 5000,
            DefaultTimeoutMs = 30000,
            MaxOutputBytes = 1048576
        };
        opts.Validate();
    }

    [Fact]
    public void BashOptions_EmptyExecutablePath_Throws()
    {
        var opts = new BashOptions { ExecutablePath = "", DiscoveryTimeoutMs = 5000, DefaultTimeoutMs = 30000, MaxOutputBytes = 1048576 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("ExecutablePath", ex.Message);
    }

    [Fact]
    public void BashOptions_DiscoveryTimeoutMsZero_Throws()
    {
        var opts = new BashOptions { ExecutablePath = "/bin/bash", DiscoveryTimeoutMs = 0, DefaultTimeoutMs = 30000, MaxOutputBytes = 1048576 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("DiscoveryTimeoutMs", ex.Message);
    }

    [Fact]
    public void BashOptions_DefaultTimeoutMsZero_Throws()
    {
        var opts = new BashOptions { ExecutablePath = "/bin/bash", DiscoveryTimeoutMs = 5000, DefaultTimeoutMs = 0, MaxOutputBytes = 1048576 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("DefaultTimeoutMs", ex.Message);
    }

    [Fact]
    public void BashOptions_MaxOutputBytesZero_Throws()
    {
        var opts = new BashOptions { ExecutablePath = "/bin/bash", DiscoveryTimeoutMs = 5000, DefaultTimeoutMs = 30000, MaxOutputBytes = 0 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("MaxOutputBytes", ex.Message);
    }

    // ── MemoryOptions ──

    [Fact]
    public void MemoryOptions_Valid_DoesNotThrow()
    {
        var opts = new MemoryOptions { ProtectedBlockTypes = ["agent_data"] };
        opts.Validate();
    }

    [Fact]
    public void MemoryOptions_EmptyProtectedBlockTypes_Throws()
    {
        var opts = new MemoryOptions { ProtectedBlockTypes = [] };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("ProtectedBlockTypes", ex.Message);
    }

    // ── TodoOptions ──

    [Fact]
    public void TodoOptions_Valid_DoesNotThrow()
    {
        var opts = new TodoOptions
        {
            ValidStatuses = ["pending", "done"],
            DefaultStatus = "pending",
            DefaultPriority = "medium"
        };
        opts.Validate();
    }

    [Fact]
    public void TodoOptions_EmptyValidStatuses_Throws()
    {
        var opts = new TodoOptions { ValidStatuses = [], DefaultStatus = "pending", DefaultPriority = "medium" };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("ValidStatuses", ex.Message);
    }

    [Fact]
    public void TodoOptions_EmptyDefaultStatus_Throws()
    {
        var opts = new TodoOptions { ValidStatuses = ["pending"], DefaultStatus = "", DefaultPriority = "medium" };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("DefaultStatus", ex.Message);
    }

    [Fact]
    public void TodoOptions_EmptyDefaultPriority_Throws()
    {
        var opts = new TodoOptions { ValidStatuses = ["pending"], DefaultStatus = "pending", DefaultPriority = "" };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("DefaultPriority", ex.Message);
    }

    [Fact]
    public void TodoOptions_DefaultStatusNotInValidStatuses_Throws()
    {
        var opts = new TodoOptions { ValidStatuses = ["pending"], DefaultStatus = "completed", DefaultPriority = "medium" };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("DefaultStatus", ex.Message);
    }

    // ── AgentOptions ──

    [Fact]
    public void AgentOptions_Valid_DoesNotThrow()
    {
        var opts = new AgentOptions { MaxToolIterations = 25, AgentName = "Glyphite.MainAgent" };
        opts.Validate();
    }

    [Fact]
    public void AgentOptions_MaxToolIterationsZero_Throws()
    {
        var opts = new AgentOptions { MaxToolIterations = 0, AgentName = "Glyphite.MainAgent" };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("MaxToolIterations", ex.Message);
    }

    [Fact]
    public void AgentOptions_MaxToolIterationsNegative_Throws()
    {
        var opts = new AgentOptions { MaxToolIterations = -1, AgentName = "Glyphite.MainAgent" };
        Assert.Throws<InvalidOperationException>(() => opts.Validate());
    }

    [Fact]
    public void AgentOptions_EmptyAgentName_Throws()
    {
        var opts = new AgentOptions { MaxToolIterations = 25, AgentName = "" };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("AgentName", ex.Message);
    }

    // ── SearchOptions ──

    [Fact]
    public void SearchOptions_Valid_DoesNotThrow()
    {
        var opts = new SearchOptions
        {
            MaxResultCount = 100,
            MaxTextFileSize = 1048576,
            MaxLineLength = 500,
            DetectBinarySampleSize = 512,
            MaxEnumerationFiles = 50000
        };
        opts.Validate();
    }

    [Fact]
    public void SearchOptions_MaxResultCountZero_Throws()
    {
        var opts = new SearchOptions { MaxResultCount = 0, MaxTextFileSize = 1, MaxLineLength = 1, DetectBinarySampleSize = 1, MaxEnumerationFiles = 1 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("MaxResultCount", ex.Message);
    }

    [Fact]
    public void SearchOptions_MaxTextFileSizeZero_Throws()
    {
        var opts = new SearchOptions { MaxResultCount = 1, MaxTextFileSize = 0, MaxLineLength = 1, DetectBinarySampleSize = 1, MaxEnumerationFiles = 1 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("MaxTextFileSize", ex.Message);
    }

    [Fact]
    public void SearchOptions_MaxLineLengthZero_Throws()
    {
        var opts = new SearchOptions { MaxResultCount = 1, MaxTextFileSize = 1, MaxLineLength = 0, DetectBinarySampleSize = 1, MaxEnumerationFiles = 1 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("MaxLineLength", ex.Message);
    }

    [Fact]
    public void SearchOptions_DetectBinarySampleSizeZero_Throws()
    {
        var opts = new SearchOptions { MaxResultCount = 1, MaxTextFileSize = 1, MaxLineLength = 1, DetectBinarySampleSize = 0, MaxEnumerationFiles = 1 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("DetectBinarySampleSize", ex.Message);
    }

    [Fact]
    public void SearchOptions_MaxEnumerationFilesZero_Throws()
    {
        var opts = new SearchOptions { MaxResultCount = 1, MaxTextFileSize = 1, MaxLineLength = 1, DetectBinarySampleSize = 1, MaxEnumerationFiles = 0 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("MaxEnumerationFiles", ex.Message);
    }

    // ── DataOptions ──

    [Fact]
    public void DataOptions_Valid_DoesNotThrow()
    {
        var opts = new DataOptions { Directory = "/data", DatabaseFileName = "glyphite.db" };
        opts.Validate();
    }

    [Fact]
    public void DataOptions_EmptyDirectory_Throws()
    {
        var opts = new DataOptions { Directory = "", DatabaseFileName = "glyphite.db" };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("Directory", ex.Message);
    }

    [Fact]
    public void DataOptions_EmptyDatabaseFileName_Throws()
    {
        var opts = new DataOptions { Directory = "/data", DatabaseFileName = "" };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("DatabaseFileName", ex.Message);
    }

    // ── CompressionOptions ──

    [Fact]
    public void CompressionOptions_Valid_DoesNotThrow()
    {
        var opts = new CompressionOptions
        {
            AutoThreshold = 50,
            CacheHitRateThreshold = 80,
            CostSignificantThreshold = 0.01
        };
        opts.Validate();
    }

    [Fact]
    public void CompressionOptions_AutoThresholdBelowZero_Throws()
    {
        var opts = new CompressionOptions { AutoThreshold = -1, CacheHitRateThreshold = 80, CostSignificantThreshold = 0.01 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("AutoThreshold", ex.Message);
    }

    [Fact]
    public void CompressionOptions_AutoThresholdAbove100_Throws()
    {
        var opts = new CompressionOptions { AutoThreshold = 101, CacheHitRateThreshold = 80, CostSignificantThreshold = 0.01 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("AutoThreshold", ex.Message);
    }

    [Fact]
    public void CompressionOptions_AutoThresholdAtZero_DoesNotThrow()
    {
        var opts = new CompressionOptions { AutoThreshold = 0, CacheHitRateThreshold = 80, CostSignificantThreshold = 0.01 };
        opts.Validate();
    }

    [Fact]
    public void CompressionOptions_AutoThresholdAt100_DoesNotThrow()
    {
        var opts = new CompressionOptions { AutoThreshold = 100, CacheHitRateThreshold = 80, CostSignificantThreshold = 0.01 };
        opts.Validate();
    }

    [Fact]
    public void CompressionOptions_CacheHitRateThresholdBelowZero_Throws()
    {
        var opts = new CompressionOptions { AutoThreshold = 50, CacheHitRateThreshold = -1, CostSignificantThreshold = 0.01 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("CacheHitRateThreshold", ex.Message);
    }

    [Fact]
    public void CompressionOptions_CacheHitRateThresholdAbove100_Throws()
    {
        var opts = new CompressionOptions { AutoThreshold = 50, CacheHitRateThreshold = 101, CostSignificantThreshold = 0.01 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("CacheHitRateThreshold", ex.Message);
    }

    [Fact]
    public void CompressionOptions_CacheHitRateThresholdAtZero_DoesNotThrow()
    {
        var opts = new CompressionOptions { AutoThreshold = 50, CacheHitRateThreshold = 0, CostSignificantThreshold = 0.01 };
        opts.Validate();
    }

    [Fact]
    public void CompressionOptions_CostSignificantThresholdNegative_Throws()
    {
        var opts = new CompressionOptions { AutoThreshold = 50, CacheHitRateThreshold = 80, CostSignificantThreshold = -0.01 };
        var ex = Assert.Throws<InvalidOperationException>(() => opts.Validate());
        Assert.Contains("CostSignificantThreshold", ex.Message);
    }

    [Fact]
    public void CompressionOptions_CostSignificantThresholdZero_DoesNotThrow()
    {
        var opts = new CompressionOptions { AutoThreshold = 50, CacheHitRateThreshold = 80, CostSignificantThreshold = 0 };
        opts.Validate();
    }
}
