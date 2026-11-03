using System.Text.Json;
using Glyphite.Host.Tools;
using Glyphite.Tests.Unit.Support;
using Xunit;

namespace Glyphite.Tests.Unit.Tools;

/// <summary>
/// The text boundary of a pocket tool — the part that used to be unreachable from tests because it
/// sat behind bash: template substitution, quoting, arg JSON, the model-facing schema and cards.
/// </summary>
public class PocketSchemaTests
{
    // ── Substitution ──

    [Fact]
    public void Substitute_QuotesValuesByDefault()
    {
        var cmd = PocketSchema.Substitute("cat {file}", Values(("file", "a b")), [PocketFixtures.Arg("file")]);

        Assert.Equal("cat 'a b'", cmd);
    }

    [Fact]
    public void Substitute_RawArg_IsInsertedUnquoted()
    {
        var cmd = PocketSchema.Substitute("cat {file}", Values(("file", "a b")), [PocketFixtures.Arg("file", raw: true)]);

        Assert.Equal("cat a b", cmd);
    }

    [Fact]
    public void Substitute_MissingOptional_UsesDefault()
    {
        var cmd = PocketSchema.Substitute("cat {file}", Values(), [PocketFixtures.Arg("file", def: "default.txt")]);

        Assert.Equal("cat default.txt", cmd);
    }

    [Fact]
    public void Substitute_MissingWithoutDefault_BecomesEmptyQuotedString()
    {
        // Not an empty hole: `cat {file}` with no value must not collapse into `cat`, which would read
        // stdin. The required-arg check happens before this, so an empty value is a deliberate choice.
        var cmd = PocketSchema.Substitute("cat {file}", Values(), [PocketFixtures.Arg("file")]);

        Assert.Equal("cat ''", cmd);
    }

    [Fact]
    public void Substitute_LeavesShellExpansionsAlone()
    {
        var cmd = PocketSchema.Substitute("echo ${HOME} {home}", Values(("home", "nope")), [PocketFixtures.Arg("home")]);

        Assert.Equal("echo ${HOME} nope", cmd);
    }

    [Fact]
    public void Substitute_MatchesPlaceholderCaseInsensitively()
    {
        var cmd = PocketSchema.Substitute("cat {FILE}", Values(("file", "x")), [PocketFixtures.Arg("file")]);

        Assert.Equal("cat x", cmd);
    }

    // ── Quoting ──

    [Theory]
    [InlineData("a-b_c/d:e=1@2%3+4,5.6")]
    [InlineData("scene.py")]
    [InlineData("Scene")]
    public void ShellQuote_LeavesSafeValuesAlone(string value)
        => Assert.Equal(value, PocketSchema.ShellQuote(value));

    [Fact]
    public void ShellQuote_QuotesSpacesAndMetacharacters()
    {
        Assert.Equal("'a b'", PocketSchema.ShellQuote("a b"));
        Assert.Equal("'a$b'", PocketSchema.ShellQuote("a$b"));
        Assert.Equal("'a;rm -rf /'", PocketSchema.ShellQuote("a;rm -rf /"));
    }

    [Fact]
    public void ShellQuote_Empty_IsAnEmptyQuotedString()
        => Assert.Equal("''", PocketSchema.ShellQuote(""));

    [Fact]
    public void ShellQuote_EscapesEmbeddedSingleQuote()
        => Assert.Equal("'it'\\''s'", PocketSchema.ShellQuote("it's"));

    // ── Arg JSON ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseArgs_Blank_IsAnEmptyList(string? json)
    {
        var args = PocketSchema.ParseArgs(json);

        Assert.NotNull(args);
        Assert.Empty(args!);
    }

    [Fact]
    public void ParseArgs_ParsesFields_AndNormalizesNames()
    {
        var args = PocketSchema.ParseArgs("""[{"name":"  File  ","desc":"d","req":true,"def":"x","raw":true}]""");

        var arg = Assert.Single(args!);
        Assert.Equal("file", arg.Name);
        Assert.Equal("d", arg.Desc);
        Assert.True(arg.Req);
        Assert.Equal("x", arg.Def);
        Assert.True(arg.Raw);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"name":"file"}""")]          // array expected
    [InlineData("""[{"name":"my-tool"}]""")]     // dash is not allowed in an arg name
    [InlineData("""[{"name":""}]""")]
    public void ParseArgs_RejectsMalformedOrInvalid(string json)
        => Assert.Null(PocketSchema.ParseArgs(json));

    [Fact]
    public void ParseArgsObject_ReturnsCaseInsensitiveValues()
    {
        var args = PocketSchema.ParseArgsObject("""{"File":"scene.py"}""");

        Assert.NotNull(args);
        Assert.True(args!.ContainsKey("FILE"));
        Assert.Equal("scene.py", PocketSchema.ArgToString(args["file"]));
    }

    [Fact]
    public void ParseArgsObject_Empty_IsAnEmptyDictionary()
    {
        var args = PocketSchema.ParseArgsObject(null);

        Assert.NotNull(args);
        Assert.Empty(args!);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("""{"file":}""")]
    [InlineData("not json")]
    public void ParseArgsObject_RejectsNonObjectOrMalformed(string json)
        => Assert.Null(PocketSchema.ParseArgsObject(json));

    // ── Model-facing schema & cards ──

    [Fact]
    public void BuildSchema_ListsPropertiesDefaultsAndRequired()
    {
        var entry = PocketFixtures.Entry("t", "echo {a} {b}",
            PocketFixtures.Arg("a", req: true, desc: "first"),
            PocketFixtures.Arg("b", def: "x", desc: ""));

        var schema = PocketSchema.BuildSchema(entry);

        Assert.Equal("object", schema.GetProperty("type").GetString());
        var props = schema.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("a").GetProperty("type").GetString());
        Assert.Equal("first", props.GetProperty("a").GetProperty("description").GetString());
        Assert.Equal("x", props.GetProperty("b").GetProperty("default").GetString());
        Assert.False(props.GetProperty("b").TryGetProperty("description", out _));
        Assert.Equal(new[] { "a" }, schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void BuildSchema_NoArgs_HasNoRequiredList()
    {
        var schema = PocketSchema.BuildSchema(PocketFixtures.Entry("t", "echo"));

        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
        Assert.False(schema.TryGetProperty("required", out _));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void BuildDescription_IncludesArgsAndTheCommand()
    {
        var entry = PocketFixtures.Entry("t", "echo {env}", PocketFixtures.Arg("env", req: true), PocketFixtures.Arg("x"));

        var desc = PocketSchema.BuildDescription(entry);

        Assert.StartsWith("test entry", desc);
        Assert.Contains("Args: env (required), x", desc);
        Assert.Contains("Runs: echo {env}", desc);
        Assert.DoesNotContain("native name", desc); // not materialized
    }

    [Fact]
    public void BuildDescription_MaterializedGlobal_SaysSo()
    {
        var entry = PocketFixtures.Entry("t", "echo");
        entry.Materialize = true;

        Assert.Contains("GLOBAL shared tool", PocketSchema.BuildDescription(entry, global: true));
        Assert.Contains("native name 't_pocket'", PocketSchema.BuildDescription(entry, global: false));
    }

    [Fact]
    public void FindUndefinedPlaceholders_ReportsOnlyUnknownOnes()
    {
        var entry = PocketFixtures.Entry("t", "cat {file} {missing} ${HOME}", PocketFixtures.Arg("file"));

        Assert.Equal(new[] { "missing" }, PocketSchema.FindUndefinedPlaceholders(entry));
    }

    [Fact]
    public void FormatEntry_MarksApprovalAndNativeState()
    {
        var entry = PocketFixtures.Entry("t", "echo {file}",
            PocketFixtures.Arg("file", req: true, def: "x", raw: true, desc: "target"));
        entry.Materialize = true;
        entry.Cwd = "/tmp/project";
        entry.Created = "2026-11-03T00:00:00Z";

        var card = PocketSchema.FormatEntry(entry, global: false, shadowed: false);

        Assert.Contains("Name:    t", card);
        Assert.Contains("Cat:     common", card);
        Assert.Contains("Scope:   local", card);
        Assert.Contains("Safety:  approved at add", card);
        Assert.Contains("Native:  yes — 't_pocket'", card);
        Assert.Contains("Cwd:     /tmp/project", card);
        Assert.Contains("file (required, default=x, raw) — target", card);
        Assert.Contains("Created: 2026-11-03T00:00:00Z", card);
    }

    [Fact]
    public void FormatEntry_LegacyEntry_IsCheckedOnEachRun()
    {
        var entry = PocketFixtures.Entry("t", "echo");
        entry.Approved = false;

        var card = PocketSchema.FormatEntry(entry);

        Assert.Contains("Safety:  legacy — checked on each run", card);
        Assert.Contains("Native:  no — pocket only", card);
        Assert.Contains("Args:    (none)", card);
    }

    // ── Helpers ──

    private static Dictionary<string, string?> Values(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.OrdinalIgnoreCase);
}
