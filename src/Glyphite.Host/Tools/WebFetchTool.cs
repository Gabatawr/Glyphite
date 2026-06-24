using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

public static partial class WebFetchTool
{
    private sealed class FetchInvoker(IConfigService cfg, string? sessionId, string tmpDir)
    {
        [Description("Fetch the content of a web page by URL. Returns content as plain text (default) or markdown. Handles redirects automatically. Large responses are truncated (showing 1/3 from top + 2/3 from bottom) and the full content is saved to a temp file for later reading. Use for reading documentation, API specs, or any online resource needed for the task.")]
        public async Task<string> Execute(
            [Description("URL to fetch (must start with http:// or https://)")] string url,
            [Description("Output format: 'text' (default, strips HTML) or 'markdown'")] string? format = null,
            bool? peek = null,
            CancellationToken ct = default)
        {
            var opts = await cfg.GetOptionsAsync<WebFetchOptions>(WebFetchOptions.Section, sessionId);
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", opts.UserAgent);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));
            return await FetchUrl(url, format ?? opts.DefaultFormat, http, opts.MaxContentLength, tmpDir, sessionId, peek, timeoutCts.Token);
        }
    }

    public static AIFunction AsFetchFunction(IConfigService cfg, string? sessionId = null, string tmpDir = "")
        => AIFunctionFactory.Create(
            new FetchInvoker(cfg, sessionId, tmpDir).Execute,
            "fetch_web");

    internal static async Task<string> FetchUrl(
        string url,
        string format,
        HttpClient http,
        int maxContentLength,
        string tmpDir,
        string? agentId = null,
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

            if (format == "markdown")
                trimmed = StripHtmlToMarkdown(trimmed);
            else
                trimmed = StripHtmlTags(trimmed);

            if (trimmed.Length <= maxContentLength)
                return trimmed;

            // Save full content to tmp file
            var agentTmp = Path.Combine(tmpDir, SanitizeForPath(agentId ?? "unknown"));
            Directory.CreateDirectory(agentTmp);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var outPath = Path.Combine(agentTmp, $"fetch_{timestamp}.out");
            File.WriteAllText(outPath, content);

            // Build truncated view: 1/3 from top + truncation notice + 2/3 from bottom
            var topChars = maxContentLength / 3;
            var bottomChars = maxContentLength - topChars;

            ReadOnlySpan<char> span = trimmed.AsSpan();
            var top = span[..topChars];
            var bottom = span[^bottomChars..];

            var note = $"[Output truncated: showing 1/3 ({topChars} chars) and 2/3 ({bottomChars} chars) of {trimmed.Length} total]\n" +
                       $"[Full content saved to: {outPath}]\n";

            return string.Concat(top.ToString(), "\n", note, bottom.ToString());
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

    private static string SanitizeForPath(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.ToString();
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}
