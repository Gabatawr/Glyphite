namespace Glyphite.Abstractions.Models;

public abstract record TurnEvent;

public sealed record ReasoningTurnEvent(string Text, bool IsPeek) : TurnEvent;

public sealed record TextTurnEvent(string Text) : TurnEvent;

public sealed record ReasoningChunkEvent(string Chunk) : TurnEvent;

public sealed record TextChunkEvent(string Chunk) : TurnEvent;

public sealed record ToolCallTurnEvent(string Name, string Args, bool IsPeek) : TurnEvent;

public sealed record ToolResultTurnEvent(string Name, string Result) : TurnEvent;

public sealed record AutoToolTurnEvent(string Name, string Args, bool IsPeek, string Result) : TurnEvent;

/// <summary>An image became part of the conversation — referenced by path or URL in the user's message.</summary>
public sealed record ImageAttachedTurnEvent(string Description) : TurnEvent;

public sealed record UsageTurnEvent(UsageSnapshot Usage) : TurnEvent;

public sealed record TurnCompleteEvent : TurnEvent;

public sealed record TurnErrorEvent(string Message) : TurnEvent;
