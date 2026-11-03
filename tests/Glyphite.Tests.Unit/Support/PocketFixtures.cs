using System.Text.Json;
using Glyphite.Host.Tools;

namespace Glyphite.Tests.Unit.Support;

/// <summary>
/// Builders for pocket entries and args, shared by the pocket tests (which are split by the class
/// each one exercises). Serialization goes through <see cref="PocketStore.JsonOpts"/> on purpose:
/// fixtures must produce the same JSON shape the vault stores.
/// </summary>
internal static class PocketFixtures
{
    public static PocketArg Arg(string name, bool req = false, string? def = null, bool raw = false, string desc = "arg") => new()
    {
        Name = name,
        Desc = desc,
        Req = req,
        Def = def,
        Raw = raw,
    };

    public static PocketEntry Entry(string name, string cmd = "echo hi", params PocketArg[] args) => new()
    {
        Name = name,
        Desc = "test entry",
        Cmd = cmd,
        Args = args.ToList(),
        Approved = true,
    };

    public static string ToJson(PocketEntry entry) => JsonSerializer.Serialize(entry, PocketStore.JsonOpts);
}
