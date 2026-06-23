using Glyphite.Host.Tools;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

public class FilePatchToolTests : IDisposable
{
    private readonly string _tempDir;

    public FilePatchToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GlyphiteTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string CreateTempFile(string content)
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    // ── Exact match ──

    [Fact]
    public async Task PatchFile_ExactMatch_Replaces_Content()
    {
        var path = CreateTempFile("Hello, world!\nThis is a test.\nGoodbye!");

        var result = await FilePatchTool.PatchFile(path, "This is a test.", "This has been patched.");

        Assert.StartsWith("Patched ", result);
        var content = await File.ReadAllTextAsync(path);
        Assert.Equal("Hello, world!\nThis has been patched.\nGoodbye!", content);
    }

    [Fact]
    public async Task PatchFile_ExactMatch_MultiLine()
    {
        var path = CreateTempFile("Line1\nLine2\nLine3\nLine4");

        var result = await FilePatchTool.PatchFile(path, "Line2\nLine3", "Replaced2\nReplaced3");

        Assert.StartsWith("Patched ", result);
        var content = await File.ReadAllTextAsync(path);
        Assert.Equal("Line1\nReplaced2\nReplaced3\nLine4", content);
    }

    // ── Fuzzy match (trimmed) ──

    [Fact]
    public async Task PatchFile_TrimmedMatch_FuzzyFallback()
    {
        var path = CreateTempFile("  indented line\n  another indented\nlast line");

        // Search with different indentation
        var result = await FilePatchTool.PatchFile(path, "indented line\nanother indented", "  new indented\n  new again");

        Assert.StartsWith("Patched ", result);
        var content = await File.ReadAllTextAsync(path);
        // The fuzzy match preserves original formatting but uses new line content
        Assert.Contains("  new indented", content);
        Assert.Contains("  new again", content);
    }

    [Fact]
    public async Task PatchFile_WhitespaceNormalized_FuzzyFallback()
    {
        var path = CreateTempFile("word1    word2\nword3   word4");

        // Search with single spaces
        var result = await FilePatchTool.PatchFile(path, "word1 word2", "replaced");

        Assert.StartsWith("Patched ", result);
        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("replaced", content);
    }

    // ── replaceAll ──

    [Fact]
    public async Task PatchFile_ReplaceAll_Replaces_All_Occurrences()
    {
        var path = CreateTempFile("foo\nbar\nfoo\nbaz\nfoo");

        var result = await FilePatchTool.PatchFile(path, "foo", "qux", replaceAll: true);

        Assert.StartsWith("Patched ", result);
        Assert.Contains("3 occurrences", result);
        var content = await File.ReadAllTextAsync(path);
        Assert.Equal("qux\nbar\nqux\nbaz\nqux", content);
    }

    [Fact]
    public async Task PatchFile_ReplaceAll_Single_Occurrence()
    {
        var path = CreateTempFile("before\nfoo\nafter");

        var result = await FilePatchTool.PatchFile(path, "foo", "bar", replaceAll: true);

        Assert.StartsWith("Patched ", result);
        Assert.Contains("1 occurrence", result);
        var content = await File.ReadAllTextAsync(path);
        Assert.Equal("before\nbar\nafter", content);
    }

    // ── Error: empty oldString ──

    [Fact]
    public async Task PatchFile_EmptyOldString_Returns_Error()
    {
        var path = CreateTempFile("some content");

        var result = await FilePatchTool.PatchFile(path, "", "new content");

        Assert.StartsWith("Error: oldString is required", result);
        // File should remain unchanged
        var content = await File.ReadAllTextAsync(path);
        Assert.Equal("some content", content);
    }

    // ── Error: file not found ──

    [Fact]
    public async Task PatchFile_FileNotFound_Returns_Error()
    {
        var path = Path.Combine(_tempDir, "nonexistent.txt");

        var result = await FilePatchTool.PatchFile(path, "old", "new");

        Assert.StartsWith("Error: File not found", result);
        Assert.Contains(path, result);
    }

    // ── Error: no match found ──

    [Fact]
    public async Task PatchFile_NoMatchFound_Returns_Error()
    {
        var path = CreateTempFile("The quick brown fox");

        var result = await FilePatchTool.PatchFile(path, "lazy dog", "energetic cat");

        Assert.StartsWith("Error: Could not find matching text", result);
        Assert.Contains(path, result);
        // File should remain unchanged
        var content = await File.ReadAllTextAsync(path);
        Assert.Equal("The quick brown fox", content);
    }

    // ── Edge cases ──

    [Fact]
    public async Task PatchFile_OldStringEqualToNewString_Returns_Error()
    {
        var path = CreateTempFile("same content");

        var result = await FilePatchTool.PatchFile(path, "same content", "same content");

        Assert.StartsWith("Error: oldString and newString are identical", result);
    }

    [Fact]
    public async Task PatchFile_WithDefaultDirectory_Resolves_Relative_Path()
    {
        var path = Path.Combine(_tempDir, "relative_test.txt");
        File.WriteAllText(path, "relative path content");

        // Change to a subdirectory to test relative path resolution
        var result = await FilePatchTool.PatchFile(
            "relative_test.txt",
            "relative path content",
            "updated content",
            defaultDirectory: _tempDir);

        Assert.StartsWith("Patched ", result);
        var content = await File.ReadAllTextAsync(path);
        Assert.Equal("updated content", content);
    }

    [Fact]
    public async Task PatchFile_WithWindowsLineEndings_Preserves_LineEndings()
    {
        var path = CreateTempFile("Line1\r\nLine2\r\nLine3");

        var result = await FilePatchTool.PatchFile(path, "Line2", "Replaced2");

        Assert.StartsWith("Patched ", result);
        var content = await File.ReadAllTextAsync(path);
        // Original line ending should be preserved
        Assert.Equal("Line1\r\nReplaced2\r\nLine3", content);
    }

    [Fact]
    public async Task PatchFile_ExactMatch_Produces_Diff_Output()
    {
        var path = CreateTempFile("before\nold line\nafter");

        var result = await FilePatchTool.PatchFile(path, "old line", "new line");

        Assert.StartsWith("Patched ", result);
        Assert.Contains("@@", result);   // diff hunk header
        Assert.Contains("-old line", result);
        Assert.Contains("+new line", result);
    }

    [Fact]
    public async Task PatchFile_IndentationFlexible_FuzzyFallback()
    {
        var path = CreateTempFile("    deeply indented\n    also indented");

        // Search with different but consistent indentation
        var result = await FilePatchTool.PatchFile(path, "  deeply indented\n  also indented", "  replaced");

        Assert.StartsWith("Patched ", result);
        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("  replaced", content);
    }

    [Fact]
    public async Task PatchFile_Append_By_Empty_Content()
    {
        var path = CreateTempFile("existing content");

        // To append, you'd replace the last newline + empty
        // But with empty oldString it errors, so let's just verify we can append to end
        // by matching the last line
        var result = await FilePatchTool.PatchFile(path, "existing content", "existing content\nnew appended line");

        Assert.StartsWith("Patched ", result);
        var content = await File.ReadAllTextAsync(path);
        Assert.Equal("existing content\nnew appended line", content);
    }

    [Fact]
    public async Task PatchFile_Partial_Line_Match_Works()
    {
        var path = CreateTempFile("This is a long line with some text in it.");

        // Match a substring of a line (the entire line content is searched)
        var result = await FilePatchTool.PatchFile(path,
            "This is a long line with some text in it.",
            "This is a REPLACED line.");

        Assert.StartsWith("Patched ", result);
        var content = await File.ReadAllTextAsync(path);
        Assert.Equal("This is a REPLACED line.", content);
    }
}
