using Glyphite.Cli;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

// Handle -v / --version before any DI initialization
if (args.Length > 0 && (args[0] == "-v" || args[0] == "--version"))
{
    var ver = Assembly.GetEntryAssembly()?.GetName()?.Version;
    if (ver is not null)
        Console.WriteLine($"{ver.Major}.{ver.Minor}.{ver.Build}");
    else
        Console.WriteLine("0.0.0");
    return;
}

// ── Ensure Glyphite.json exists BEFORE building the host ──
// This is critical so that Bootstrapper.AddJsonFile() can pick it up
// with reloadOnChange=true. If we created it after host build (as was done
// in SessionManager), LazyChatClient via IOptionsMonitor would never see it.
var cwd = Directory.GetCurrentDirectory();
var cwdConfig = Path.Combine(cwd, "Glyphite.json");
if (!File.Exists(cwdConfig))
{
    var defaults = """
{
  "Glyphite": {
    "LLM": {
      "Endpoint": "https://api.deepseek.com/v1",
      "ApiKey": "",
      "Model": "deepseek-v4-flash",
      "ContextWindow": 1000000,
      "ReasoningEffort": "High",
      "Models": [
        { "Name": "deepseek-v4-flash", "Miss": 0.14, "Hit": 0.0028, "Output": 0.28 },
        { "Name": "deepseek-v4-pro", "Miss": 0.435, "Hit": 0.003625, "Output": 0.87 }
      ]
    }
  }
}
""";
    File.WriteAllText(cwdConfig, defaults);
}

try
{
    var host = Bootstrapper.BuildHost(args);
    var repl = host.Services.GetRequiredService<ChatRepl>();

    using var cts = new CancellationTokenSource();
    await repl.RunAsync(cts.Token);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\n[!] Fatal error: {ex.Message}");
    var inner = ex.InnerException;
    while (inner is not null)
    {
        Console.WriteLine($"  ├─ Inner: [{inner.GetType().Name}] {inner.Message}");
        inner = inner.InnerException;
    }
    Console.ResetColor();
    Serilog.Log.Error(ex, "Fatal error");
    Serilog.Log.CloseAndFlush();
}
