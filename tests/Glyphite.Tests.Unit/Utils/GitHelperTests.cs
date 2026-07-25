using Glyphite.Host.Utils;
using Xunit;

namespace Glyphite.Tests.Unit.Utils;

public class GitHelperTests
{
    [Fact]
    public void ParseUnifiedDiff_Empty_ReturnsEmpty()
    {
        var result = GitHelper.ParseUnifiedDiff("");
        Assert.Null(result.LineStatus);
        Assert.Null(result.DeletedLines);
    }

    [Fact]
    public void ParseUnifiedDiff_NoHunks_ReturnsEmpty()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc..def 100644
            --- a/file.txt
            +++ b/file.txt
            """;
        var result = GitHelper.ParseUnifiedDiff(diff);
        Assert.Null(result.LineStatus);
        Assert.Null(result.DeletedLines);
    }

    [Fact]
    public void ParseUnifiedDiff_SingleHunk_AddedLines()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc..def 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,4 +1,5 @@
             line one
             line two
            +new line
             line three
             line four
            """;
        var result = GitHelper.ParseUnifiedDiff(diff);
        Assert.NotNull(result.LineStatus);
        var singleKvp = Assert.Single(result.LineStatus);
        Assert.Equal(3, singleKvp.Key);
        Assert.Equal('+', singleKvp.Value);
        Assert.Null(result.DeletedLines);
    }

    [Fact]
    public void ParseUnifiedDiff_SingleHunk_DeletedLines()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc..def 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,4 +1,3 @@
             line one
             line two
            -old line
             line three
            """;
        var result = GitHelper.ParseUnifiedDiff(diff);
        Assert.Null(result.LineStatus);
        Assert.NotNull(result.DeletedLines);
        Assert.Single(result.DeletedLines);
        Assert.Equal(3, result.DeletedLines[0].OldLineNum);
        Assert.Equal("old line", result.DeletedLines[0].Content);
    }

    [Fact]
    public void ParseUnifiedDiff_SingleHunk_ModifiedLines()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc..def 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,5 +1,5 @@
             context
            -old
            +new
             context
             context
            """;
        var result = GitHelper.ParseUnifiedDiff(diff);
        Assert.NotNull(result.LineStatus);
        var statusKvp = Assert.Single(result.LineStatus);
        Assert.Equal(2, statusKvp.Key);
        Assert.Equal('+', statusKvp.Value);

        Assert.NotNull(result.DeletedLines);
        Assert.Single(result.DeletedLines);
        Assert.Equal(2, result.DeletedLines[0].OldLineNum);
        Assert.Equal("old", result.DeletedLines[0].Content);
    }

    [Fact]
    public void ParseUnifiedDiff_MultipleHunks_TracksLineNumbers()
    {
        // Hunk 1: lines 1-5 in old, 1-5 in new — line 2 modified (old->new)
        // Hunk 2: lines 10-12 in old, 10-13 in new — added line12new shifts old line12 to new line13
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc..def 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,5 +1,5 @@
             line1
            -old2
            +new2
             line3
             line4
             line5
            @@ -10,3 +10,4 @@
             line10
             line11
            +line12new
             line12
            """;
        var result = GitHelper.ParseUnifiedDiff(diff);
        Assert.NotNull(result.LineStatus);
        Assert.Equal(2, result.LineStatus.Count);
        // Hunk 1: line 2 was modified
        Assert.Equal('+', result.LineStatus[2]);
        // Hunk 2: line 12 is the newly added line (old line12 shifted to new line13)
        Assert.Equal('+', result.LineStatus[12]);

        Assert.NotNull(result.DeletedLines);
        Assert.Single(result.DeletedLines); // only one deletion from hunk 1
        Assert.Equal(2, result.DeletedLines[0].OldLineNum);
        Assert.Equal("old2", result.DeletedLines[0].Content);
    }

    [Fact]
    public void ParseUnifiedDiff_IgnoreBinary_ReturnsEmpty()
    {
        var diff = """
            diff --git a/img.png b/img.png
            index abc..def 100644
            Binary files a/img.png and b/img.png differ
            """;
        var result = GitHelper.ParseUnifiedDiff(diff);
        Assert.Null(result.LineStatus);
        Assert.Null(result.DeletedLines);
    }

    [Fact]
    public void ParseUnifiedDiff_NoNewlineAtEnd_NoCrash()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc..def 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,3 +1,4 @@
             line1
            +line2
             line3
            \ No newline at end of file
            """;
        var result = GitHelper.ParseUnifiedDiff(diff);
        Assert.NotNull(result.LineStatus);
        var lastKvp = Assert.Single(result.LineStatus);
        Assert.Equal(2, lastKvp.Key);
        Assert.Equal('+', lastKvp.Value);
        Assert.Null(result.DeletedLines);
    }
}
