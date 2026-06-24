using System.Text;
using Glyphite.Abstractions.Models;
using Glyphite.Cli.Services;
using Microsoft.Extensions.AI;

namespace Glyphite.Cli;

public partial class ChatRepl
{
    private string _liveChunkType = ""; // "" | "reasoning" | "text"

    // Streaming text buffer for table detection
    private readonly StringBuilder _streamTextBuffer = new();
    private readonly List<string> _streamTableLines = new();
    private bool _streamInTable;

    /// <summary>Flush any buffered table and reset table state.</summary>
    private void FlushStreamTable()
    {
        if (_streamTableLines.Count == 0) return;

        var formatted = TableRenderer.TryFormatTable(_streamTableLines);
        if (formatted is not null)
        {
            Console.Write(formatted);
            // Mark as text so mode transitions work correctly
            _liveChunkType = "text";
        }
        else
        {
            // Failed to parse as table — emit raw lines
            foreach (var line in _streamTableLines)
                Console.WriteLine(line);
        }

        _streamTableLines.Clear();
        _streamInTable = false;
    }

    /// <summary>Process streaming text: buffer to lines, detect &amp; format tables.</summary>
    private void RenderStreamingText(string chunk, string type, ref RenderState s)
    {
        // Reasoning chunks — write directly (no table processing needed)
        if (type == "reasoning")
        {
            if (_streamInTable) FlushStreamTable();

            // Mode switch: text/reasoning → reasoning
            if (_liveChunkType != "reasoning")
            {
                if (_liveChunkType != "")
                    Console.WriteLine();
                else if (s.wasTool || s.wasText || s.wasReasoning)
                    Console.WriteLine();
                _liveChunkType = "reasoning";
            }

            Console.ForegroundColor = _renderer.ReasoningColor;
            Console.Write(chunk);
            Console.ResetColor();
            s.wasReasoning = true;
            s.wasText = false;
            s.wasTool = false;
            s.lineStart = false;
            return;
        }

        // Text chunks — buffer and process per-line
        // Mode switch: reasoning → text (or any other → text)
        if (_liveChunkType != "text")
        {
            if (_liveChunkType != "")
                Console.WriteLine();
            else if (s.wasTool || s.wasText || s.wasReasoning)
                Console.WriteLine();
            _liveChunkType = "text";
        }

        _streamTextBuffer.Append(chunk);

        var buffer = _streamTextBuffer.ToString();
        var lastNewline = buffer.LastIndexOf('\n');

        if (lastNewline >= 0)
        {
            // Process all complete lines (up to and including the last \n)
            var completePart = buffer[..lastNewline];
            _streamTextBuffer.Clear();
            _streamTextBuffer.Append(buffer[(lastNewline + 1)..]);

            foreach (var rawLine in completePart.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');

                if (TableRenderer.IsTableRow(line))
                {
                    _streamTableLines.Add(line);
                    _streamInTable = true;
                }
                else if (_streamInTable && TableRenderer.IsTableSeparator(line))
                {
                    // Separator line inside a table
                    _streamTableLines.Add(line);
                }
                else
                {
                    // Non-table line — flush any buffered table first
                    if (_streamInTable)
                        FlushStreamTable();

                    // Write non-table line normally
                    Console.ForegroundColor = _renderer.AgentColor;
                    Console.WriteLine(rawLine);
                    Console.ResetColor();
                }
            }

            s.wasText = true;
            s.wasReasoning = false;
            s.wasTool = false;
            s.lineStart = false;
        }
        // else: no complete line yet — keep buffering, don't write raw
    }

    private void RenderChunk(string chunk, string type, ref RenderState s)
    {
        // Delegate all rendering to RenderStreamingText which handles both text and reasoning,
        // mode switches, table detection, and line buffering.
        RenderStreamingText(chunk, type, ref s);
    }

    /// <summary>Flush any remaining buffered text at end of streaming.</summary>
    private void FlushStreamBuffer()
    {
        // Flush any accumulated table
        if (_streamInTable)
        {
            // If there's a buffered incomplete line that looks like a table row,
            // add it to the table lines before flushing.
            if (_streamTextBuffer.Length > 0)
            {
                var bufferedLine = _streamTextBuffer.ToString().TrimEnd('\r');
                if (TableRenderer.IsTableRow(bufferedLine) || TableRenderer.IsTableSeparator(bufferedLine))
                {
                    _streamTableLines.Add(bufferedLine);
                    _streamTextBuffer.Clear();
                }
            }

            FlushStreamTable();
        }

        // Flush any incomplete line in buffer (non-table text)
        if (_streamTextBuffer.Length > 0)
        {
            Console.ForegroundColor = _renderer.AgentColor;
            Console.Write(_streamTextBuffer.ToString());
            Console.ResetColor();
            _streamTextBuffer.Clear();
        }
    }

    /// <summary>Reset all streaming buffer state between turns.</summary>
    private void ResetStreamBuffers()
    {
        _streamTextBuffer.Clear();
        _streamTableLines.Clear();
        _streamInTable = false;
    }

    private async Task ProcessInputAsync(string input, ChatOptions chatOptions, CancellationToken ct)
    {
        var s = new RenderState();
        _liveChunkType = "";
        ResetStreamBuffers();

        try
        {
            var enumerable = TurnProcessor.ProcessAsync(AgentId!, input, chatOptions, ct);
            await using var enumerator = enumerable.GetAsyncEnumerator();

            var hasNext = await enumerator.MoveNextAsync();

            // Config is now loaded from disk — refresh renderer with fresh ToolStreamingOptions
            await _renderer.RefreshAsync(AgentId!);

            while (hasNext)
            {
                var turnEvent = enumerator.Current;
                switch (turnEvent)
                {
                    case ReasoningChunkEvent rc:
                        RenderChunk(rc.Chunk, "reasoning", ref s);
                        break;

                    case TextChunkEvent tc:
                        RenderChunk(tc.Chunk, "text", ref s);
                        break;

                    case ReasoningTurnEvent r:
                        FlushStreamBuffer();
                        _renderer.RenderBlock(MemoryBlock.AgentReasoning(r.Text), ref s);
                        _liveChunkType = "";
                        break;

                    case TextTurnEvent t:
                        FlushStreamBuffer();
                        _renderer.RenderBlock(MemoryBlock.AgentMessage(t.Text), ref s);
                        _liveChunkType = "";
                        break;

                    case AutoToolTurnEvent at:
                        FlushStreamBuffer();
                        _liveChunkType = "";
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"[AutoTool: {at.Name} | {at.Args}]");
                        var autoLen = _renderer.CurrentStreamOpts.GetMaxLength(at.Name, -1);
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
                        FlushStreamBuffer();
                        _liveChunkType = "";
                        var callBlock = MemoryBlock.ToolCall(tc.Name, tc.Args);
                        _renderer.RenderBlock(callBlock, ref s);
                        break;

                    case ToolResultTurnEvent tr:
                        _liveChunkType = "";
                        if (!string.IsNullOrEmpty(tr.Result))
                        {
                            var maxLen = _renderer.CurrentStreamOpts.GetMaxLength(tr.Name, -1);
                            if (maxLen == 0) { /* hidden */ }
                            else
                            {
                                var display = maxLen > 0 && tr.Result.Length > maxLen
                                    ? tr.Result[..maxLen]
                                    : tr.Result;
                                // Format any markdown tables in tool results (subagents may return tables)
                                display = TableRenderer.RenderTables(display);
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

                hasNext = await enumerator.MoveNextAsync();
            }

            // Flush any remaining buffered text (tables, incomplete lines)
            FlushStreamBuffer();
        }
        catch (OperationCanceledException)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n[!] Operation cancelled by user.");
            Console.ResetColor();

            // Per-iteration usage was written to DB by OnIterationRecorded.
            // Update prompt state from last completed iteration (more accurate than stale values).
            UpdateFromLastIteration();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[!] Error: {ex.Message}");
            Console.ResetColor();

            UpdateFromLastIteration();
        }
    }
}
