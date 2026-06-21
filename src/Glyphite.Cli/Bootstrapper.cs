using System.Reflection;
using System.Text;
using Glyphite.Abstractions.Models;
using Glyphite.Cli.Services;
using Glyphite.Host.DI;
using Glyphite.Host.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace Glyphite.Cli;

public static class Bootstrapper
{
    public static IHost BuildHost(string[] args)
    {
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logsDir);
        var dateStr = DateTime.Now.ToString("dd-MM-yyyy");
        var existingLogs = Directory.GetFiles(logsDir, $"{dateStr}-*.log").Length;
        var runNumber = existingLogs + 1;
        var logFile = Path.Combine(logsDir, $"{dateStr}-{runNumber}.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logFile, outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var host = global::Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((ctx, cfg) =>
            {
                // 1. Встроенный appsettings.json из ресурсов
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("Glyphite.Cli.appsettings.json");
                if (stream is not null)
                {
                    var json = new StreamReader(stream).ReadToEnd();
                    cfg.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
                }

                // 2. Переменная окружения DEEPSEEK_API_KEY
                var envKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
                if (!string.IsNullOrEmpty(envKey))
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Glyphite:DeepSeek:ApiKey"] = envKey
                    });

                // 3. appsettings.Development.json рядом с бинарём
                var devJson = Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json");
                if (File.Exists(devJson))
                    cfg.AddJsonFile(devJson, optional: false, reloadOnChange: false);

                // 4. Glyphite.json в рабочей директории
                var cwdJson = Path.Combine(Directory.GetCurrentDirectory(), "Glyphite.json");
                if (File.Exists(cwdJson))
                    cfg.AddJsonFile(cwdJson, optional: false, reloadOnChange: false);
            })
            .ConfigureServices((ctx, services) =>
            {
                // Replace ConsoleLifetime with NoopLifetime (no Ctrl+C hijacking)
                services.RemoveAll<IHostLifetime>();
                services.AddSingleton<IHostLifetime, NoopLifetime>();

                // ── Core services via Host DI extension ──
                services.AddGlyphiteHost(ctx.Configuration)
                        .AddMcp();

                // ── UI ──
                services.AddSingleton(sp =>
                {
                    var streamOpts = sp.GetRequiredService<IOptions<ToolStreamingOptions>>().Value;
                    return new ConsoleRenderer(streamOpts);
                });
                services.AddSingleton<ChatRepl>();
            })
            .Build();

        return host;
    }

    private sealed class NoopLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
