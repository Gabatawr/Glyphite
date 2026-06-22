using System.ClientModel;
using System.Reflection;
using System.Text;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Host.Data;
using Glyphite.Host.Memory;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Services;
using Glyphite.Host.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Glyphite.Host.DI;

public static class HostServiceCollectionExtensions
{
    public static IServiceCollection AddGlyphiteHost(this IServiceCollection services, IConfiguration configuration)
    {
        var glConfig = configuration.GetSection("Glyphite");

        // ── Options (consumed via IOptions<T>) ──
        RegisterOptions<DeepSeekOptions>(services, glConfig, o => o.Validate());
        RegisterOptions<AgentOptions>(services, glConfig, o => o.Validate());
        RegisterOptions<MemoryOptions>(services, glConfig, o => o.Validate());
        RegisterOptions<BashOptions>(services, glConfig, o => o.Validate());
        RegisterOptions<CompressionOptions>(services, glConfig, o => o.Validate());
        // остальные секции (WebFetch, Search, Todo, ContentDedup, McpServers)
        // потребляются через свежий _cfgService.GetOptionsAsync<T>() каждый turn

        // ── Data directory ──
        var dataOpts = glConfig.GetSection(DataOptions.Section).Get<DataOptions>()
            ?? throw new InvalidOperationException("Data section missing");
        var baseDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(baseDir, dataOpts.Directory);
        Directory.CreateDirectory(dataDir);

        // ── Data ──
        services.AddSingleton<MemoryStore>(sp =>
            MemoryStore.CreateForApp(dataDir, dataOpts.DatabaseFileName));
        services.AddSingleton<IAgentStore>(sp => sp.GetRequiredService<MemoryStore>());
        services.AddSingleton<IBlockStore>(sp => sp.GetRequiredService<MemoryStore>());
        services.AddSingleton<IConfigStore>(sp => sp.GetRequiredService<MemoryStore>());

        // ── IChatClient ──
        services.AddSingleton<IChatClient>(sp =>
        {
            var deepseek = sp.GetRequiredService<IOptions<DeepSeekOptions>>().Value;
            var client = new OpenAIClient(
                new ApiKeyCredential(deepseek.ApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(deepseek.Endpoint) });
            return client.GetChatClient(deepseek.Model).AsIChatClient();
        });

        // ── Services ──
        services.AddSingleton<IBashSessionManager>(sp =>
        {
            var cfgService = sp.GetRequiredService<IConfigService>();
            var bashOpts = sp.GetRequiredService<IOptions<BashOptions>>().Value;
            var defaultDir = !string.IsNullOrEmpty(bashOpts.DefaultDirectory)
                ? Path.GetFullPath(bashOpts.DefaultDirectory)
                : Directory.GetCurrentDirectory();
            return new BashSessionManager(cfgService, bashOpts, defaultDir);
        });

        services.AddSingleton<IConfigService>(sp =>
        {
            var store = sp.GetRequiredService<IConfigStore>();
            return new ConfigService(store, configuration.GetSection("Glyphite"));
        });

        services.AddSingleton<IAgentManager>(sp =>
        {
            var store = sp.GetRequiredService<IAgentStore>();
            var cfg = sp.GetRequiredService<IConfigService>();
            return new AgentManager(store, cfg);
        });

        services.AddSingleton<SubAgentManager>();
        services.AddSingleton<CompactionService>();
        services.AddSingleton<ISubAgentConfigLoader, SubAgentConfigLoader>();

        // ── Agent Scope (per-agent DI scope) ──
        services.AddSingleton<IAgentScopeFactory, AgentScopeFactory>();

        // ── Scoped per-agent services (new scope per /new, /use, /clone, sub-agent) ──
        services.AddScoped<ITurnProcessor, TurnProcessor>();
        services.AddScoped<IBlockMemoryProvider>(sp =>
        {
            var agentStore = sp.GetRequiredService<IAgentStore>();
            var blockStore = sp.GetRequiredService<IBlockStore>();
            var cfgService = sp.GetRequiredService<IConfigService>();
            var memOpts = sp.GetRequiredService<IOptions<MemoryOptions>>().Value;
            var agentOpts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            var deepseek = sp.GetRequiredService<IOptions<DeepSeekOptions>>().Value;
            var compOpts = sp.GetRequiredService<IOptions<CompressionOptions>>().Value;
            return new BlockMemoryProvider(agentStore, blockStore, cfgService, memOpts, agentOpts, deepseek.Model, compOpts);
        });
        services.AddScoped<IToolRegistry, ToolRegistry>();

        return services;
    }

    private static void RegisterOptions<TOptions>(
        IServiceCollection services,
        IConfigurationSection glConfig,
        Action<TOptions>? validate = null)
        where TOptions : class, new()
    {
        var section = glConfig.GetSection(
            (string?)typeof(TOptions).GetField("Section")?.GetValue(null)
            ?? throw new InvalidOperationException($"{typeof(TOptions).Name} is missing a 'Section' const"));

        var optionsBuilder = services.AddOptions<TOptions>()
            .Bind(section);

        if (validate is not null)
        {
            optionsBuilder
                .Validate(o => { validate(o); return true; })
                .ValidateOnStart();
        }
    }
}
