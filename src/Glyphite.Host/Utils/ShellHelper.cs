namespace Glyphite.Host.Utils;

/// <summary>Helpers for building shell command strings safely.</summary>
public static class ShellHelper
{
    /// <summary>Wrap a value in single quotes, escaping embedded quotes for bash.</summary>
    public static string QuoteSingle(string value)
        => "'" + value.Replace("'", "'\\''") + "'";
}
