namespace Glyphite.Host.Images;

/// <summary>
/// Collects images produced by tools during a turn.
///
/// The provider accepts images only in <c>user</c> messages, so a tool cannot hand one back
/// through its tool-role result. Instead <see cref="Tools.ImageTool"/> drops the payload here and
/// the chat pipeline drains the sink after each tool batch, injecting a single user message with
/// every image collected so far (see <see cref="Services.FailSafeChatClient"/>).
///
/// Scoped per agent scope — the same lifetime as the <c>TurnProcessor</c> and <c>ToolRegistry</c>
/// that share it.
/// </summary>
public sealed class ImageAttachmentSink
{
    /// <summary>An image waiting to be delivered, with the optionally-attached question that asked for it.</summary>
    public sealed record Pending(ImagePayload Payload, string? Note);

    private readonly Lock _gate = new();
    private readonly List<Pending> _pending = [];

    public void Add(ImagePayload payload, string? note = null)
    {
        lock (_gate) _pending.Add(new Pending(payload, note));
    }

    public int Count
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>Take everything queued and empty the sink. Returns an empty list when nothing is pending.</summary>
    public IReadOnlyList<Pending> Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return [];
            var taken = _pending.ToArray();
            _pending.Clear();
            return taken;
        }
    }

    /// <summary>Drop anything queued — called at the start of every turn so a cancelled turn can't leak into the next.</summary>
    public void Clear()
    {
        lock (_gate) _pending.Clear();
    }
}
