using Glyphite.Abstractions.Models;
using Glyphite.Host.Tools;
using Glyphite.Tests.Unit.Support;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

public class FileReadToolTests
{
    private static ContentDedupOptions Dedup() => new()
    {
        MinLines = 3,
        FrequencyThreshold = 0.05,
        MinLineLength = 32,
        MaxAliases = 10,
        AutoDedupExtensions = [".log"],
    };

    [Fact]
    public async Task ImageFile_IsRefused_WithAPointerToViewImage()
    {
        using var dir = new TempDir();
        var path = dir.Write("shot.png", TestImages.Png(1920, 1080));

        var result = await FileReadTool.ReadFile(path, Dedup(), maxSize: null);

        Assert.StartsWith("Error:", result);
        Assert.Contains("image/png", result);
        Assert.Contains("view_image", result);
        Assert.DoesNotContain("\u0089PNG", result);
    }

    [Fact]
    public async Task ImageBytes_AreRefused_EvenWhenTheExtensionIsMisleading()
    {
        using var dir = new TempDir();
        var path = dir.Write("notes.txt", TestImages.Png1x1);

        var result = await FileReadTool.ReadFile(path, Dedup(), maxSize: null);

        Assert.Contains("view_image", result);
    }

    [Fact]
    public async Task TextWithAnImageExtension_IsStillRead()
    {
        // The guard keys off content, not the name — a .png that is really text stays readable.
        using var dir = new TempDir();
        var path = dir.Write("fake.png", "line one\nline two\n");

        var result = await FileReadTool.ReadFile(path, Dedup(), maxSize: null);

        Assert.DoesNotContain("view_image", result);
        Assert.Contains("line one", result);
        Assert.Contains("line two", result);
    }

    [Fact]
    public async Task MissingFile_StillReportsNotFound()
    {
        var result = await FileReadTool.ReadFile("/nowhere/gone.txt", Dedup(), maxSize: null);

        Assert.Contains("File not found", result);
    }
}
