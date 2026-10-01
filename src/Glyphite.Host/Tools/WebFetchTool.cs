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
            return await FetchUrl(url, format ?? opts.DefaultFormat, http, ct);
        }
    }

    public static AIFunction AsFetchFunction(IConfigService cfg, string? sessionId = null)
        => AIFunctionFactory.Create(
            new FetchInvoker(cfg, sessionId).Execute,
            "fetch_web");

    internal static async Task<string> FetchUrl(
        string url,
        string format,
        HttpClient http,
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
            using var response = await http.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();

            // A picture is not a document: decoding one as text yields mojibake, so refuse it and
            // point at the tool that can actually show it. Sniffing the bytes catches servers that
            // mislabel an image; the declared type catches the ones that label it correctly.
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (FindImageMediaType(bytes, response.Content.Headers.ContentType?.MediaType) is { } mediaType)
                return DescribeImageInsteadOfText(uri, mediaType, bytes);

            var content = await response.Content.ReadAsStringAsync(ct);
            var trimmed = content.Trim();

            if (format == "markdown")
                trimmed = StripHtmlToMarkdown(trimmed);
            else
                trimmed = StripHtmlTags(trimmed);

            return trimmed;
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
        var text = HtmlTagRegex().Replace(html, " ");
        text = WhitespaceRegex().Replace(text, " ");
        return text.Trim();
    }

    private static string StripHtmlToMarkdown(string html)
    {
        var text = StripHtmlTags(html);
        return text;
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}
