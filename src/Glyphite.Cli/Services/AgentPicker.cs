namespace Glyphite.Cli.Services;

/// <summary>Helpers for interactive agent selection from a list.</summary>
internal static class AgentPicker
{
    /// <summary>Parse user input as either a 1-based index or a direct name match.</summary>
    public static int? TryParseIndex(string input, int count)
    {
        if (int.TryParse(input, out var idx) && idx >= 1 && idx <= count)
            return idx - 1;
        return null;
    }

    /// <summary>Resolve user input to a name from the list. Returns null if not found.</summary>
    public static string? Resolve(List<string> items, string? input)
    {
        if (string.IsNullOrEmpty(input)) return null;
        var idx = TryParseIndex(input, items.Count);
        if (idx is not null) return items[idx.Value];
        return items.Contains(input) ? input : null;
    }
}
