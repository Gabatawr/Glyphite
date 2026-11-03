using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Images;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static partial class WebFetchTool
{
    /// <summary>Ceiling on the bytes pulled from one URL, so a huge page cannot exhaust memory.</summary>
    internal const long DefaultMaxDownloadBytes = 8L * 1024 * 1024;

    private sealed class FetchInvoker(IConfigService cfg, string? sessionId)
    {
        [Description("Fetch the content of a web page by URL. Returns content as plain text (default) or markdown. Handles redirects automatically. Use for reading documentation, API specs, or any online resource needed for the task. This tool returns text only — it never opens a picture; a URL that turns out to be an image is refused with a pointer to `view_image`.")]
        public async Task<string> Execute(
            [Description("URL to fetch (must start with http:// or https://)")] string url,
            [Description("Output format: 'text' (default, strips HTML) or 'markdown'")] string? format = null,
            CancellationToken ct = default)
        {
            var opts = await cfg.GetOptionsAsync<WebFetchOptions>(WebFetchOptions.Section, sessionId);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", opts.UserAgent);
            return await FetchUrl(url, format ?? opts.DefaultFormat, http,
                opts.MaxContentChars, opts.MaxDownloadBytes, ct);
        }
    }

    public static AIFunction AsFetchFunction(IConfigService cfg, string? sessionId = null)
        => AIFunctionFactory.Create(
            new FetchInvoker(cfg, sessionId).Execute,
            "fetch_web");

    /// <summary>
    /// Fetch a page as text. The result is bounded twice on purpose: <paramref name="maxBytes"/>
    /// keeps a huge page from being downloaded whole, and <paramref name="maxChars"/> keeps the
    /// text itself small enough to live in the history — every later request re-sends it, so one
    /// unbounded page used to push the whole session past the model's context window.
    /// </summary>
    internal static async Task<string> FetchUrl(
        string url,
        string format,
        HttpClient http,
        int maxChars = ToolExecutionDefaults.ContentMaxSize,
        long maxBytes = DefaultMaxDownloadBytes,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Error: URL is required";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "Error: URL must start with http:// or https://";

        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // A picture is not a document: decoding one as text yields mojibake, so refuse it and
            // point at the tool that can actually show it. Sniffing the bytes catches servers that
            // mislabel an image; the declared type catches the ones that label it correctly.
            var bytes = await ReadCappedAsync(response.Content, maxBytes, ct);
            if (FindImageMediaType(bytes, response.Content.Headers.ContentType?.MediaType) is { } mediaType)
                return DescribeImageInsteadOfText(uri, mediaType, bytes);

            var content = Decode(bytes, response.Content.Headers.ContentType?.CharSet).Trim();

            var text = format == "markdown"
                ? StripHtmlToMarkdown(content)
                : StripHtmlTags(content);

            return CapText(text, maxChars);
        }
        catch (HttpRequestException ex)
        {
            return $"Error fetching URL: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return $"Error: Request timed out after {http.Timeout.TotalSeconds} seconds";
        }
    }

    /// <summary>
    /// Image container of a fetched body, or null when the body is a document.
    /// Only the containers <c>view_image</c> can actually display count — SVG, for instance,
    /// is text and stays a document.
    /// </summary>
    internal static string? FindImageMediaType(byte[] body, string? declaredMediaType)
    {
        if (ImageFormats.SniffMediaType(body) is { } sniffed)
            return sniffed;

        if (declaredMediaType is not null
            && ImageFormats.Supported.Contains(declaredMediaType, StringComparer.OrdinalIgnoreCase))
            return declaredMediaType.ToLowerInvariant();

        return null;
    }

    /// <summary>Reads the body, but never more than <paramref name="maxBytes"/> of it.</summary>
    private static async Task<byte[]> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        if (maxBytes <= 0)
            return await content.ReadAsByteArrayAsync(ct);

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];

        long total = 0;
        while (total < maxBytes)
        {
            var want = (int)Math.Min(chunk.Length, maxBytes - total);
            var read = await stream.ReadAsync(chunk.AsMemory(0, want), ct);
            if (read == 0) break;

            buffer.Write(chunk, 0, read);
            total += read;
        }

        return buffer.ToArray();
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        try
        {
            return string.IsNullOrWhiteSpace(charset)
                ? Encoding.UTF8.GetString(bytes)
                : Encoding.GetEncoding(charset).GetString(bytes);
        }
        catch (ArgumentException)
        {
            // Unknown or unsupported charset — UTF-8 is the only sane fallback left.
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// Bounds the text handed back to the model, keeping the head and the tail of the page.
    /// A negative or zero limit means "no limit".
    /// </summary>
    private static string CapText(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
            return text;

        var head = maxChars * 2 / 3;
        var tail = maxChars - head;

        return string.Concat(
            text.AsSpan(0, head),
            $"\n\n[Page truncated at {maxChars} chars. Full text length: {text.Length}. "
            + "Fetch a narrower page or section if the rest matters.]\n",
            text.AsSpan(text.Length - tail));
    }

    private static string DescribeImageInsteadOfText(Uri uri, string mediaType, byte[] body)
    {
        var dimensions = ImageFormats.ReadDimensions(body, mediaType);
        var shape = dimensions is { } d ? $"{d.Width}×{d.Height}, " : "";
        var size = ImageFormats.DescribeBytes(body.LongLength);

        return $"Error: {uri} is an image ({mediaType}, {shape}{size}), not a document — fetch_web returns text. "
             + "If you really need to look at it, open it explicitly with `view_image`.";
    }

    private static string StripHtmlTags(string html)
    {
        var text = HtmlTagRegex().Replace(StripCodeBodies(html), " ");
        text = WhitespaceRegex().Replace(text, " ");
        return text.Trim();
    }

    private static string StripHtmlToMarkdown(string html)
    {
        var text = StripHtmlTags(html);
        return text;
    }

    /// <summary>
    /// Drops <c>&lt;script&gt;</c> and <c>&lt;style&gt;</c> elements together with their bodies.
    /// Removing tags alone leaves the JavaScript in the text — on a modern docs site that is most
    /// of the page (measured: 2.6 MB out of 2.6 MB), and none of it is worth a token.
    /// </summary>
    internal static string StripCodeBodies(string html)
    {
        var sb = new StringBuilder(html.Length);
        var i = 0;

        while (i < html.Length)
        {
            var (start, end) = FindCodeElement(html, i);
            if (start < 0)
            {
                sb.Append(html, i, html.Length - i);
                break;
            }

            sb.Append(html, i, start - i);
            i = end;
        }

        return sb.ToString();
    }

    private static readonly string[] CodeElementTags = ["script", "style"];

    /// <summary>Span of the next script/style element, or (-1, -1) when the rest of the page is clean.</summary>
    private static (int Start, int End) FindCodeElement(string html, int from)
    {
        var start = -1;
        var tag = "";

        foreach (var candidate in CodeElementTags)
        {
            var p = IndexOfElementStart(html, from, candidate);
            if (p >= 0 && (start < 0 || p < start))
            {
                start = p;
                tag = candidate;
            }
        }

        if (start < 0) return (-1, -1);

        // Unterminated element: the page is broken, and the tail is script either way.
        var close = html.IndexOf("</" + tag, start, StringComparison.OrdinalIgnoreCase);
        if (close < 0) return (start, html.Length);

        var gt = html.IndexOf('>', close);
        return (start, gt < 0 ? html.Length : gt + 1);
    }

    /// <summary>
    /// Position of an opening <c>&lt;tag</c> that is really that element — <c>&lt;script&gt;</c> and
    /// <c>&lt;script src=…&gt;</c> match, a longer name such as <c>&lt;scripts&gt;</c> does not.
    /// </summary>
    private static int IndexOfElementStart(string html, int from, string tag)
    {
        var i = from;

        while (true)
        {
            var p = html.IndexOf("<" + tag, i, StringComparison.OrdinalIgnoreCase);
            if (p < 0) return -1;

            var after = p + 1 + tag.Length;
            if (after < html.Length && (html[after] == '>' || char.IsWhiteSpace(html[after])))
                return p;

            i = p + 1;
        }
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}
