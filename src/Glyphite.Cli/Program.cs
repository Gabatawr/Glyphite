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
    var assembly = Assembly.GetExecutingAssembly();
    using var stream = assembly.GetManifestResourceStream("Glyphite.Cli.appsettings.default.json")
        ?? throw new InvalidOperationException("Embedded resource 'Glyphite.Cli.appsettings.default.json' not found.");
    using var reader = new StreamReader(stream);
    var defaults = reader.ReadToEnd();
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
