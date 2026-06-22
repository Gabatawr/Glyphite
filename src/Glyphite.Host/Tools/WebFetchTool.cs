using System.ComponentModel;
using System.Text.RegularExpressions;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static partial class WebFetchTool
{
    private sealed class FetchInvoker(IConfigService cfg, string? sessionId)
    {
        [Description("Fetch the content of a web page by URL. Returns content as plain text (default) or markdown. Handles redirects automatically. Use for reading documentation, API specs, or any online resource needed for the task.")]
        public async Task<string> Execute(
            [Description("URL to fetch (must start with http:// or https://)")] string url,
            [Description("Output format: 'text' (default, strips HTML) or 'markdown'")] string? format = null,
            bool? peek = null,
            CancellationToken ct = default)
        {
            var opts = await cfg.GetOptionsAsync<WebFetchOptions>("WebFetch", sessionId);
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", opts.UserAgent);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));
            return await FetchUrl(url, format ?? opts.DefaultFormat, http, opts.MaxContentLength, peek, timeoutCts.Token);
        }
    }

    public static AIFunction AsFetchFunction(IConfigService? cfg, string? sessionId = null)
        => AIFunctionFactory.Create(
            new FetchInvoker(cfg!, sessionId).Execute,
            "fetch_web");

    internal static async Task<string> FetchUrl(
        string url,
        string format,
        HttpClient http,
        int maxContentLength,
        bool? peek = null,
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
            var response = await http.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(ct);
            var trimmed = content.Trim();

            if (trimmed.Length > maxContentLength)
                trimmed = trimmed[..maxContentLength] + $"\n\n... (truncated at {maxContentLength} characters)";

            if (format == "markdown")
            {
                trimmed = StripHtmlToMarkdown(trimmed);
            }
            else
            {
                trimmed = StripHtmlTags(trimmed);
            }

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
