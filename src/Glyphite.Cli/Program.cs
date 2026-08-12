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

// ── First-run config BEFORE building the host ──
// Ensures a global Glyphite.json exists in the install location, prompts for an
// API key when none is configured, and asks once per folder whether to use the
// global config or create a local Glyphite.json. Runs before the host build so
// Bootstrapper.AddJsonFile() picks up any created files.
FirstRunConfig.Ensure();

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
