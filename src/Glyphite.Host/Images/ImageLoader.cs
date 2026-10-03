using System.Net;
using System.Security.Cryptography;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Glyphite.Host.Images;

/// <summary>How an image travels to the provider.</summary>
public enum ImagePayloadKind
{
    /// <summary>The bytes travel inside the request body (<c>DataContent</c>).</summary>
    Inline,

    /// <summary>Only the URL travels; the provider fetches the picture (<c>UriContent</c>).</summary>
    Passthrough
}

/// <summary>One image resolved and ready to travel to the provider.</summary>
/// <param name="Content">MEAI part — <see cref="DataContent"/> when inlined, <see cref="UriContent"/> when passed through.</param>
/// <param name="Spec">The path or URL as the user/tool wrote it.</param>
/// <param name="MediaType">Detected container.</param>
/// <param name="Bytes">Inline size; 0 for pass-through URLs (the provider downloads them).</param>
/// <param name="Width">Pixel width when the header was walkable.</param>
/// <param name="Height">Pixel height when the header was walkable.</param>
/// <param name="Kind">Inline (bytes in the body) or passed through (URL handed over).</param>
/// <param name="Key">
/// Canonical identity, independent of how the reference was spelled: the resolved absolute path for a
/// local file, the URL for a remote one, a content hash for a <c>data:</c> URL. Two references that
/// name the same picture share a key, which is how the sink refuses to send it twice in one turn.
/// </param>
public sealed record ImagePayload(
    AIContent Content,
    string Spec,
    string MediaType,
    long Bytes,
    int? Width,
    int? Height,
    ImagePayloadKind Kind,
    string Key)
{
    /// <summary>True when the bytes travel inside the request body.</summary>
    public bool Inline => Kind == ImagePayloadKind.Inline;

    /// <summary>Compact human/LLM readable line: <c>path (image/png, 1920×1080, 412 KB)</c>.</summary>
    public string Describe()
    {
        var size = !Inline
            ? "URL — fetched by the provider"
            : Width is int w && Height is int h
                ? $"{w}×{h}, {ImageFormats.DescribeBytes(Bytes)}"
                : ImageFormats.DescribeBytes(Bytes);

        return $"{Spec} ({MediaType}, {size})";
    }
}

/// <summary>
/// Turns an image reference — local path, <c>http(s)</c> URL, <c>file://</c> URL or <c>data:</c> URL —
/// into an MEAI part the provider accepts. Validates format by content, enforces the provider's
/// documented limits, and (per <see cref="ImageOptions.UrlMode"/>) decides whether to inline the
/// bytes or hand the URL over.
/// </summary>
public sealed class ImageLoader
{
    /// <summary>
    /// Shared client for inline downloads. Timeout is infinite because each call applies its own
    /// deadline from <see cref="ImageOptions.DownloadTimeoutSeconds"/> via a linked token.
    /// </summary>
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All   // a gzip image must still sniff as one
        })
    {
        Timeout = Timeout.InfiniteTimeSpan
    });

    private readonly HttpClient _http;
    private readonly ILogger? _logger;

    public ImageLoader(HttpClient? http = null, ILogger? logger = null)
    {
        _http = http ?? SharedHttp.Value;
        _logger = logger;
    }

    // ── Loading ──────────────────────────────────────────────────────

    /// <summary>
    /// Resolve one reference. Returns a payload, or a human-readable error explaining exactly
    /// why the reference was rejected — errors are handed to the model, so they must be actionable.
    /// </summary>
    public async Task<(ImagePayload? Payload, string? Error)> LoadAsync(
        string spec,
        ImageOptions opts,
        string? defaultDirectory = null,
        string? detail = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return (null, "Empty image reference.");

        spec = spec.Trim();
        detail ??= opts.Detail;

        if (spec.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return LoadDataUrl(spec, opts, detail);

        if (Uri.TryCreate(spec, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https")
                return await LoadUrlAsync(uri, opts, detail, ct).ConfigureAwait(false);

            if (uri.Scheme == "file")
                return await LoadFileAsync(uri.LocalPath, spec, opts, detail, ct).ConfigureAwait(false);
        }

        return await LoadFileAsync(ResolveLocalPath(spec, defaultDirectory), spec, opts, detail, ct)
            .ConfigureAwait(false);
    }

    private async Task<(ImagePayload?, string?)> LoadFileAsync(
        string path, string spec, ImageOptions opts, string? detail, CancellationToken ct)
    {
        if (!File.Exists(path))
            return (null, $"File not found: {path}");

        var length = new FileInfo(path).Length;
        if (length == 0)
            return (null, $"File is empty: {path}");
        if (length > opts.MaxImageBytes)
            return (null, $"Image is {ImageFormats.DescribeBytes(length)}, over the {ImageFormats.DescribeBytes(opts.MaxImageBytes)} limit: {path}");

        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return Build(bytes, spec, Path.GetFullPath(path), opts, detail);
    }

    private async Task<(ImagePayload?, string?)> LoadUrlAsync(
        Uri uri, ImageOptions opts, string? detail, CancellationToken ct)
    {
        var spec = uri.ToString();

        if (spec.Length > opts.MaxUrlLength)
        {
            _logger?.LogDebug("Image URL is {Length} chars, over the {Limit} limit — inlining instead", spec.Length, opts.MaxUrlLength);
            return await DownloadAsync(uri, spec, opts, detail, ct).ConfigureAwait(false);
        }

        if (!ShouldInline(uri, opts))
        {
            var media = ImageFormats.MediaTypeFromExtension(uri.AbsolutePath) ?? "image/*";
            var content = new UriContent(uri, media);
            ApplyDetail(content, detail);
            return (new ImagePayload(content, spec, media, 0, null, null, ImagePayloadKind.Passthrough, spec), null);
        }

        return await DownloadAsync(uri, spec, opts, detail, ct).ConfigureAwait(false);
    }

    private async Task<(ImagePayload?, string?)> DownloadAsync(
        Uri uri, string spec, ImageOptions opts, string? detail, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, opts.DownloadTimeoutSeconds)));

        try
        {
            using var resp = await _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode} fetching {uri}");

            if (resp.Content.Headers.ContentLength is long declared && declared > opts.MaxImageBytes)
                return (null, $"Image at {uri} is {ImageFormats.DescribeBytes(declared)}, over the {ImageFormats.DescribeBytes(opts.MaxImageBytes)} limit.");

            var bytes = await ReadWithLimitAsync(resp, opts.MaxImageBytes, cts.Token).ConfigureAwait(false);
            if (bytes is null)
                return (null, $"Image at {uri} exceeds the {ImageFormats.DescribeBytes(opts.MaxImageBytes)} limit.");

            var (payload, error) = Build(bytes, uri.ToString(), uri.ToString(), opts, detail);
            return (payload, error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"Timed out after {opts.DownloadTimeoutSeconds}s fetching {uri}");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Could not fetch {uri}: {ex.Message}");
        }
    }

    /// <summary>Read a response body, refusing to buffer more than <paramref name="limit"/> bytes.</summary>
    private static async Task<byte[]?> ReadWithLimitAsync(
        HttpResponseMessage resp, long limit, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private (ImagePayload?, string?) LoadDataUrl(string spec, ImageOptions opts, string? detail)
    {
        var comma = spec.IndexOf(',');
        if (comma < 0)
            return (null, "Malformed data: URL — no comma separating header and payload.");

        var header = spec[..comma];
        var payload = spec[(comma + 1)..];
        var isBase64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);

        // Reject oversize before decoding — a base64 payload expands to ~3/4 its character count.
        var estimate = isBase64 ? payload.Length / 4L * 3 : payload.Length;
        if (estimate > opts.MaxImageBytes)
            return (null, $"Inline image exceeds the {ImageFormats.DescribeBytes(opts.MaxImageBytes)} limit.");

        byte[] bytes;
        try
        {
            bytes = isBase64 ? Convert.FromBase64String(payload) : PercentDecode(payload);
        }
        catch (FormatException)
        {
            return (null, "Malformed base64 payload in data: URL.");
        }

        // No name and no location to key on — identity is the bytes themselves.
        var key = "data:" + Convert.ToHexString(SHA256.HashData(bytes));
        return Build(bytes, $"data URL ({bytes.Length} bytes)", key, opts, detail);
    }

    /// <summary>
    /// Decode a non-base64 <c>data:</c> payload. Percent-escapes are raw octets, not UTF-8 text,
    /// so <c>%E2%82%AC</c> must stay three bytes instead of collapsing into one '€'.
    /// </summary>
    private static byte[] PercentDecode(string payload)
    {
        var bytes = new byte[payload.Length];
        var length = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] == '%' && i + 2 < payload.Length
                && Uri.IsHexDigit(payload[i + 1]) && Uri.IsHexDigit(payload[i + 2]))
            {
                bytes[length++] = (byte)((HexValue(payload[i + 1]) << 4) | HexValue(payload[i + 2]));
                i += 2;
            }
            else
            {
                bytes[length++] = (byte)payload[i];
            }
        }

        return bytes[..length];
    }

    private static int HexValue(char c)
        => c <= '9' ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10;

    /// <summary>Validate bytes, read dimensions, and wrap them as an inline part.</summary>
    private (ImagePayload?, string?) Build(byte[] bytes, string spec, string key, ImageOptions opts, string? detail)
    {
        var media = ImageFormats.SniffMediaType(bytes);
        if (media is null)
            return (null, $"Not a supported image (JPEG, PNG, GIF, WebP): {spec}");

        if (bytes.LongLength > opts.MaxImageBytes)
            return (null, $"Image is {ImageFormats.DescribeBytes(bytes.LongLength)}, over the {ImageFormats.DescribeBytes(opts.MaxImageBytes)} limit: {spec}");

        var dims = ImageFormats.ReadDimensions(bytes, media);
        if (dims is { } d && (d.Width > opts.MaxDimension || d.Height > opts.MaxDimension))
        {
            return (null,
                $"{d.Width}×{d.Height} exceeds the {opts.MaxDimension}px per-side limit: {spec}");
        }

        var content = new DataContent(bytes, media);
        ApplyDetail(content, detail);

        return (new ImagePayload(content, spec, media, bytes.LongLength, dims?.Width, dims?.Height, ImagePayloadKind.Inline, key), null);
    }

    /// <summary>
    /// Attach the provider's detail hint. <c>auto</c> (or nothing) omits the field entirely and
    /// lets the provider decide — which is equivalent to <c>original</c> today.
    /// </summary>
    public static void ApplyDetail(AIContent content, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail) || string.Equals(detail, "auto", StringComparison.OrdinalIgnoreCase))
            return;

        content.AdditionalProperties ??= [];
        content.AdditionalProperties["detail"] = detail.ToLowerInvariant();
    }

    /// <summary>Resolve a relative path the same way the file tools do — against the agent's working directory.</summary>
    public static string ResolveLocalPath(string path, string? defaultDirectory)
    {
        path = OSHelper.NormalizePath(path);
        if (Path.IsPathRooted(path))
            return path;
        return Path.GetFullPath(Path.Combine(defaultDirectory ?? Directory.GetCurrentDirectory(), path));
    }

    /// <summary>
    /// Whether the agent must fetch the image itself instead of handing the URL to the provider.
    ///
    /// <c>auto</c> (default) is adaptive: a public URL is handed over — the provider downloads it,
    /// so no bytes travel through the agent — while a host the provider can never reach
    /// (localhost, private ranges, intranet names) is fetched here and inlined. The trade-off is
    /// real: a handed-over URL the provider fails to download (404, auth, hotlink protection) kills
    /// the whole request with an opaque 400, leaving the model nothing to react to. <c>inline</c>
    /// spends bandwidth to avoid exactly that.
    ///
    /// A URL past <see cref="ImageOptions.MaxUrlLength"/> is inlined regardless of mode (handled
    /// before this check), because the provider would reject it.
    ///
    /// Matching is case-insensitive, mirroring <see cref="ImageOptions.Validate"/>.
    /// </summary>
    private static bool ShouldInline(Uri uri, ImageOptions opts)
    {
        var mode = opts.UrlMode?.Trim().ToLowerInvariant();
        return mode switch
        {
            "inline" => true,
            _ => IsNonPublicHost(uri.Host)   // auto — adaptive
        };
    }

    /// <summary>Suffixes that mark a name as living inside a private network.</summary>
    private static readonly string[] IntranetSuffixes =
        [".local", ".internal", ".lan", ".corp", ".home", ".test", ".localhost", ".intranet"];

    internal static bool IsNonPublicHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return true;

        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host == "::1" || host == "0.0.0.0") return true;

        foreach (var suffix in IntranetSuffixes)
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;

        var parts = host.Split('.');
        if (parts.Length == 4 && parts.All(p => byte.TryParse(p, out _)))
        {
            return parts[0] switch
            {
                "10" => true,
                "127" => true,
                "192" when parts[1] == "168" => true,
                "172" when byte.TryParse(parts[1], out var second) && second is >= 16 and <= 31 => true,
                "169" when parts[1] == "254" => true,
                _ => false
            };
        }

        // A bare name with no dot is an intranet host — not reachable from the provider.
        return !host.Contains('.');
    }

    // ── Scanning user input ──────────────────────────────────────────

    /// <summary>
    /// Find image references in a message: quoted runs first (they survive spaces inside paths),
    /// then whitespace-separated tokens with wrapping punctuation trimmed. Only references that
    /// are certainly images qualify — a path must exist and sniff as one, a URL must look like one.
    /// </summary>
    public static IReadOnlyList<string> ExtractSpecs(string? text, ImageOptions opts, string? defaultDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (!opts.Enabled || !opts.AutoAttach) return [];

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in EnumerateCandidates(text))
        {
            if (found.Count >= opts.MaxImagesPerRequest) break;
            if (candidate.Length == 0) continue;
            if (!seen.Add(candidate)) continue;
            if (IsImageSpec(candidate, opts, defaultDirectory)) found.Add(candidate);
        }

        return found;
    }

    private static IEnumerable<string> EnumerateCandidates(string text)
    {
        foreach (var quoted in QuotedRuns(text))
            yield return quoted;

        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            yield return Trim(token);
    }

    private static IEnumerable<string> QuotedRuns(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var quote = text[i];
            if (quote is not ('"' or '\'' or '`')) continue;

            var end = text.IndexOf(quote, i + 1);
            if (end < 0) break;

            if (end > i + 1)
                yield return text[(i + 1)..end].Trim();

            i = end;
        }
    }

    private const string LeadingTrim = "\"'`([{<>,;:!?*";
    private const string TrailingTrim = "\"'`)]}>,;:!?*.";

    private static string Trim(string token)
        => token.Trim().Trim(LeadingTrim.ToCharArray()).TrimEnd(TrailingTrim.ToCharArray()).Trim();

    private static bool IsImageSpec(string candidate, ImageOptions opts, string? defaultDirectory)
    {
        if (candidate.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https")
            {
                switch (opts.UrlMatching.ToLowerInvariant())
                {
                    case "never": return false;
                    case "always": return true;
                    default: return HasImageExtension(uri.AbsolutePath, opts);
                }
            }

            if (uri.Scheme == "file")
                return HasImageExtension(uri.LocalPath, opts) && IsLocalImage(uri.LocalPath);
        }

        if (!HasImageExtension(candidate, opts)) return false;
        return IsLocalImage(ResolveLocalPath(candidate, defaultDirectory));
    }

    private static bool HasImageExtension(string path, ImageOptions opts)
        => opts.Extensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static bool IsLocalImage(string path) => ImageFormats.TrySniffFile(path) is not null;
}
