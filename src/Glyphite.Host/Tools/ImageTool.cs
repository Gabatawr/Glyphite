using System.ComponentModel;
using Glyphite.Abstractions.Interfaces;
using Glyphite.Abstractions.Models;
using Glyphite.Host.Images;
using Glyphite.Host.Tools;
using Microsoft.Extensions.AI;

namespace Glyphite.Host.Tools;

/// <summary>
/// <c>view_image</c> — lets the agent actually look at a picture, by local path or URL.
///
/// The provider accepts images only in user messages, so the tool does not return one: it loads
/// the image, queues it on the <see cref="ImageAttachmentSink"/>, and answers with a one-line
/// receipt. The chat pipeline then injects the queued images as a user message, which is what
/// makes them visible to the model.
/// </summary>
public static class ImageTool
{
    public const string ToolName = "view_image";

    public static AIFunction AsViewImageFunction(
        ImageLoader loader,
        ImageAttachmentSink sink,
        IConfigService cfg,
        string? defaultDirectory = null,
        string? sessionId = null)
        => AIFunctionFactory.Create(
            new ViewInvoker(loader, sink, cfg, defaultDirectory, sessionId).Execute,
            ToolName);

    private sealed class ViewInvoker(
        ImageLoader loader,
        ImageAttachmentSink sink,
        IConfigService cfg,
        string? defaultDirectory,
        string? sessionId)
    {
        [Description("""
            Load an image so you can actually see it. Use this whenever the answer depends on what
            is in a picture — a screenshot you just took, a chart, a diagram, a UI mockup, a photo.
            Reading an image with read_file or fetch_web returns nothing useful — they are text-only
            and will refuse an image, pointing back here.

            The image is attached to your next message and you will see it directly, so after calling
            this tool just look, then answer. Supported formats: JPEG, PNG, GIF, WebP.
            """)]
        public async Task<string> Execute(
            [Description("Where the image is: a local file path, an http(s) URL, or a data: URL.")]
            string source,
            [Description("Detail level: auto (default), low, high, original. Use low for big screenshots when fine detail does not matter.")]
            string? detail = null,
            [Description("Optional: what you are looking for in the image. Written next to the image to keep the request focused.")]
            string? question = null,
            CancellationToken ct = default)
        {
            var opts = await cfg.GetOptionsAsync<ImageOptions>(ImageOptions.Section, sessionId);

            if (!opts.Enabled)
                return "Error: image support is disabled (Image:Enabled = false in Glyphite.json).";

            var (payload, error) = await loader.LoadAsync(source, opts, defaultDirectory, detail, ct);

            if (payload is null)
                return $"Error: {error}";

            sink.Add(payload, question);

            var imageWord = payload.Inline ? "The image is attached" : "The image URL is attached";
            var asked = string.IsNullOrWhiteSpace(question) ? "" : $" Question: {question}";
            return $"{imageWord} to your next message — look at it directly and answer.{asked} [{payload.Describe()}]";
        }
    }
}
