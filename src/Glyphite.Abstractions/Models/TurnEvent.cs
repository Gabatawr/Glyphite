namespace Glyphite.Abstractions.Models;

public abstract record TurnEvent;

public sealed record ReasoningTurnEvent(string Text, bool IsPeek) : TurnEvent;

public sealed record TextTurnEvent(string Text) : TurnEvent;

public sealed record ReasoningChunkEvent(string Chunk) : TurnEvent;

public sealed record TextChunkEvent(string Chunk) : TurnEvent;

public sealed record ToolCallTurnEvent(string Name, string Args, bool IsPeek) : TurnEvent;

public sealed record ToolResultTurnEvent(string Name, string Result) : TurnEvent;

public sealed record AutoToolTurnEvent(string Name, string Args, bool IsPeek, string Result) : TurnEvent;

public sealed record UsageTurnEvent(long CacheHitTokens, long CacheMissTokens, long OutputTokenCount, long LastHitTokens = 0, long LastMissTokens = 0) : TurnEvent;

public sealed record TurnCompleteEvent : TurnEvent;

public sealed record TurnErrorEvent(string Message) : TurnEvent;
