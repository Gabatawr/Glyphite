using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
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
    return 0;
}

// ── Headless mode: glyphite "task" | glyphite "agent-name" "task" ──
// One-shot run without interactive first-run prompts: the caller is expected to have
// a configured API key (global ~/.glyphite/Glyphite.json or a local config).
// A missing key fails fast with a clear error instead of prompting. Exit codes:
// 0 = success, 1 = turn/run error, 2 = invalid arguments/agent name.
if (args.Length == 1 || args.Length == 2)
{
    var task = args[^1];
    var agentName = args.Length == 2 ? args[0] : null;

    if (string.IsNullOrWhiteSpace(task))
    {
        Console.Error.WriteLine("Usage: glyphite \"<task>\" | glyphite \"<agent-name>\" \"<task>\"");
        return 2;
    }

    try
    {
        using var host = Bootstrapper.BuildHost(args);

        var cfgService = host.Services.GetRequiredService<IConfigService>();
        var llm = await cfgService.GetOptionsAsync<LlmOptions>(LlmOptions.Section);
        if (string.IsNullOrWhiteSpace(llm.ApiKey))
        {
            Console.Error.WriteLine("[!] LLM API key is not configured. Run 'glyphite' interactively once to set it up.");
            return 1;
        }

        var exitCode = await HeadlessRunner.RunAsync(task, agentName, host.Services, CancellationToken.None);
        Serilog.Log.CloseAndFlush();
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[!] Fatal error: {ex.Message}");
        var inner = ex.InnerException;
        while (inner is not null)
        {
            Console.Error.WriteLine($"  ├─ Inner: [{inner.GetType().Name}] {inner.Message}");
            inner = inner.InnerException;
        }
        Serilog.Log.Error(ex, "Fatal error (headless)");
        Serilog.Log.CloseAndFlush();
        return 1;
    }
}

if (args.Length > 2)
{
    Console.Error.WriteLine("Usage: glyphite \"<task>\" | glyphite \"<agent-name>\" \"<task>\" | (no args — interactive REPL)");
    return 2;
}

// ── Interactive REPL: first-run config BEFORE building the host ──
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
    return 1;
}
return 0;