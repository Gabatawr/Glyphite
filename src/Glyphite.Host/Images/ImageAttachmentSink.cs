using Glyphite.Abstractions.Models;

namespace Glyphite.Host.Images;

/// <summary>Outcome of offering an image to the sink.</summary>
public enum ImageAddResult
{
    /// <summary>Queued — it rides in the next injected user message.</summary>
    Added,

    /// <summary>The same picture already rides in this turn's conversation, so a second copy would only cost tokens.</summary>
    AlreadyAttached,

    /// <summary>Queuing it would push the request past the provider's budget.</summary>
    OverBudget
}

/// <summary>
/// Collects images produced by tools during a turn, keeps the turn inside the provider's request
/// budget, and refuses to attach the same picture twice.
///
/// The provider accepts images only in <c>user</c> messages, so a tool cannot hand one back
/// through its tool-role result. Instead <see cref="Tools.ImageTool"/> drops the payload here and
/// the chat pipeline drains the sink after each tool batch, injecting a single user message with
/// every image collected so far (see <see cref="Services.FailSafeChatClient"/>).
///
/// Scope is per agent — the same lifetime as the <c>TurnProcessor</c> and <c>ToolRegistry</c>
/// that share it — and <see cref="Clear"/> runs at the start of every turn, so the budget counters
/// and the dedup set cover one turn. Images already injected stay in the conversation and are
/// re-sent on every later iteration, so the binding limit is the running total, not one message.
/// </summary>
public sealed class ImageAttachmentSink
{
    /// <summary>An image waiting to be delivered, with the optionally-attached question that asked for it.</summary>
    public sealed record Pending(ImagePayload Payload, string? Note);

    private readonly Lock _gate = new();
    private readonly List<Pending> _pending = [];
    private readonly HashSet<string> _present = new(StringComparer.Ordinal);
    private int _accepted;
    private long _inlineBytes;

    /// <summary>Queue an image without checking the budget or the dedup set. Prefer <see cref="TryAdd"/>.</summary>
    public void Add(ImagePayload payload, string? note = null)
    {
        lock (_gate) _pending.Add(new Pending(payload, note));
    }

    /// <summary>
    /// Register the images that already ride in this turn's opening user message, so they count
    /// against the same budget and a tool cannot attach a second copy of any of them.
    /// </summary>
    public void Seed(IEnumerable<ImagePayload> attached)
    {
        lock (_gate)
        {
            foreach (var payload in attached)
            {
                _accepted++;
                if (payload.Inline) _inlineBytes += payload.Bytes;
                _present.Add(payload.Key);
            }
        }
    }

    /// <summary>
    /// Queue an image when it is not already in the conversation and the turn still has room under
    /// the provider's request budget — image count and total inline bytes. Callers turn
    /// <see cref="ImageAddResult.AlreadyAttached"/> into a "look, it is already here" answer and
    /// <see cref="ImageAddResult.OverBudget"/> into a tool-level error, so the turn survives
    /// instead of the provider rejecting the whole request.
    /// </summary>
    public ImageAddResult TryAdd(ImagePayload payload, string? note, ImageOptions opts, out string? reason)
    {
        lock (_gate)
        {
            // A picture the model asked for twice — or asked for after the user already attached it —
            // is already visible in this request. Sending it again buys nothing and costs tokens.
            if (_present.Contains(payload.Key))
            {
                reason = "already attached to this turn's conversation";
                return ImageAddResult.AlreadyAttached;
            }

            if (_accepted >= opts.MaxImagesPerRequest)
            {
                reason = $"this turn already carries {opts.MaxImagesPerRequest} images "
                       + "(Image:MaxImagesPerRequest)";
                return ImageAddResult.OverBudget;
            }

            if (payload.Inline && _inlineBytes + payload.Bytes > opts.MaxTotalBytes)
            {
                reason = $"the request body would exceed {ImageFormats.DescribeBytes(opts.MaxTotalBytes)} "
                       + "(Image:MaxTotalBytes)";
                return ImageAddResult.OverBudget;
            }

            _accepted++;
            if (payload.Inline) _inlineBytes += payload.Bytes;
            _present.Add(payload.Key);
            _pending.Add(new Pending(payload, note));
            reason = null;
            return ImageAddResult.Added;
        }
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

    public int Count
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>Drop anything queued and forget the turn — called at the start of every turn so a
    /// cancelled turn can neither leak an image nor block one as "already attached" later.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _present.Clear();
            _accepted = 0;
            _inlineBytes = 0;
        }
    }
}
