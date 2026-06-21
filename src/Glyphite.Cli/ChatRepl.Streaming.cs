using Glyphite.Abstractions.Models;
using Glyphite.Cli.Services;
using Microsoft.Extensions.AI;

namespace Glyphite.Cli;

public partial class ChatRepl
{
    private string _liveChunkType = ""; // "" | "reasoning" | "text"

    private void RenderChunk(string chunk, string type, ref RenderState s)
    {
        if (_liveChunkType != type)
        {
            if (_liveChunkType != "")
                Console.WriteLine(); // switching mode
            else if (s.wasTool || s.wasText || s.wasReasoning)
                Console.WriteLine(); // after block content
        }

        _liveChunkType = type;
        Console.ForegroundColor = type == "reasoning"
            ? _renderer.ReasoningColor
            : _renderer.AgentColor;
        Console.Write(chunk);
        Console.ResetColor();

        if (type == "reasoning")
        {
            s.wasReasoning = true;
            s.wasText = false;
            s.wasTool = false;
        }
        else
        {
            s.wasText = true;
            s.wasReasoning = false;
            s.wasTool = false;
        }
        s.lineStart = false;
    }

    private async Task ProcessInputAsync(string input, ChatOptions chatOptions, CancellationToken ct)
    {
        await _renderer.RefreshAsync();
        var s = new RenderState();
        _liveChunkType = "";

        try
        {
            await foreach (var turnEvent in _turnProcessor
                .ProcessAsync(_agentId!, input, chatOptions, ct))
            {
                switch (turnEvent)
                {
                    case ReasoningChunkEvent rc:
                        RenderChunk(rc.Chunk, "reasoning", ref s);
                        break;

                    case TextChunkEvent tc:
                        RenderChunk(tc.Chunk, "text", ref s);
                        break;

                    case ReasoningTurnEvent r:
                        _renderer.RenderBlock(MemoryBlock.AgentReasoning(r.Text), ref s);
                        _liveChunkType = "";
                        break;

                    case TextTurnEvent t:
                        _renderer.RenderBlock(MemoryBlock.AgentMessage(t.Text), ref s);
                        _liveChunkType = "";
                        break;

                    case AutoToolTurnEvent at:
                        _liveChunkType = "";
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"[AutoTool: {at.Name} | {at.Args}]");
                        var autoLen = _renderer.CurrentStreamOpts.ToolMaxLength.GetValueOrDefault(at.Name, -1);
                        if (autoLen != 0 && !string.IsNullOrEmpty(at.Result))
                        {
                            var display = autoLen > 0 && at.Result.Length > autoLen
                                ? at.Result[..autoLen]
                                : at.Result;
                            Console.WriteLine(display);
                        }
                        Console.ResetColor();
                        Console.WriteLine();
                        break;

                    case ToolCallTurnEvent tc:
                        _liveChunkType = "";
                        var callBlock = MemoryBlock.ToolCall(tc.Name, tc.Args);
                        _renderer.RenderBlock(callBlock, ref s);
                        break;

                    case ToolResultTurnEvent tr:
                        _liveChunkType = "";
                        if (!string.IsNullOrEmpty(tr.Result))
                        {
                            var maxLen = _renderer.CurrentStreamOpts.ToolMaxLength.GetValueOrDefault(tr.Name, -1);
                            if (maxLen == 0) { /* hidden */ }
                            else
                            {
                                var display = maxLen > 0 && tr.Result.Length > maxLen
                                    ? tr.Result[..maxLen]
                                    : tr.Result;
                                Console.ForegroundColor = _renderer.ToolResultColor;
                                Console.WriteLine(display.TrimEnd('\n', '\r'));
                                Console.ResetColor();
                            }
                        }
                        break;

                    case TurnErrorEvent err:
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n[!] {err.Message}");
                        Console.ResetColor();
                        break;

                    case UsageTurnEvent ue:
                        _lastTurnHit = ue.CacheHitTokens;
                        _lastTurnMiss = ue.CacheMissTokens;
                        _lastTurnOutput = ue.OutputTokenCount;
                        UpdatePromptInline(ue.CacheHitTokens, ue.CacheMissTokens, ue.OutputTokenCount, ue.LastHitTokens, ue.LastMissTokens);
                        break;

                    case TurnCompleteEvent:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n[!] Operation cancelled by user.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[!] Error: {ex.Message}");
            Console.ResetColor();
        }
    }
}
