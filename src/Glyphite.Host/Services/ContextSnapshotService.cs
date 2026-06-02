using System.Text;

namespace Glyphite.Host.Services;

public class ContextSnapshotService
{
    private string? _previousText;
    private string _currentText = string.Empty;
    private string? _diff;
    private int _firstBreakLine;

    public string? PreviousText => _previousText;
    public string CurrentText => _currentText;
    public string? Diff => _diff;
    public int FirstBreakLine => _firstBreakLine;
    public int TotalLines { get; private set; }
    public int DeltaChars { get; private set; }
    public bool HasChanges => _diff is not null;

    public void Update(string contextText)
    {
        _previousText = _currentText.Length > 0 ? _currentText : null;

        if (_previousText is not null)
        {
            _diff = GenerateDiff(_previousText, contextText);
            _firstBreakLine = FindFirstBreakLine(_previousText, contextText);
            DeltaChars = contextText.Length - _previousText.Length;
        }
        else
        {
            _diff = null;
            _firstBreakLine = 0;
            DeltaChars = 0;
        }

        _currentText = contextText;
        TotalLines = contextText.Length > 0 ? contextText.Split('\n').Length : 0;
    }

    private static int FindFirstBreakLine(string oldText, string newText)
    {
        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');
        var start = 0;
        while (start < oldLines.Length && start < newLines.Length && oldLines[start] == newLines[start])
            start++;
        return start < oldLines.Length || start < newLines.Length ? start + 1 : 0;
    }

    private static string? GenerateDiff(string oldText, string newText)
    {
        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');

        int start = 0;
        while (start < oldLines.Length && start < newLines.Length && oldLines[start] == newLines[start])
            start++;

        if (start == oldLines.Length && start == newLines.Length)
            return null;

        int oldEnd = oldLines.Length - 1;
        int newEnd = newLines.Length - 1;
        while (oldEnd >= start && newEnd >= start && oldLines[oldEnd] == newLines[newEnd])
        {
            oldEnd--;
            newEnd--;
        }

        var oldCount = oldEnd - start + 1;
        var newCount = newEnd - start + 1;

        var sb = new StringBuilder();
        sb.AppendLine($"@@ -{start + 1},{oldCount} +{start + 1},{newCount} @@");

        var ctxBefore = Math.Max(0, start - 3);
        for (int i = ctxBefore; i < start; i++)
            sb.AppendLine($" {oldLines[i]}");

        for (int i = start; i <= oldEnd; i++)
            sb.AppendLine($"-{oldLines[i]}");

        for (int i = start; i <= newEnd; i++)
            sb.AppendLine($"+{newLines[i]}");

        var ctxAfter = Math.Min(newLines.Length - 1, newEnd + 3);
        for (int i = newEnd + 1; i <= ctxAfter; i++)
            sb.AppendLine($" {newLines[i]}");

        return sb.ToString().TrimEnd();
    }
}
