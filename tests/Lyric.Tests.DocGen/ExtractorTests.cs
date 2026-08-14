using System.Runtime.CompilerServices;
using System.Text;
using Lyric.DocGen;
using Lyric.DocGen.Extraction;
using Lyric.DocGen.Model;

namespace Lyric.Tests.DocGen;

/// <summary>
/// What lands in the model, and what deliberately does not.
///
/// <para>The filter is the substance here: a reference documents the PUBLIC surface. Both halves are
/// tested — that a public item arrives, and that a private one does not. A test holding only the
/// first would pass an extractor that documents everything.</para>
/// </summary>
public class ExtractorTests
{
    private static bool UpdateMode =>
        Environment.GetEnvironmentVariable("LYRIC_UPDATE_SNAPSHOTS") is "1" or "true";

    private static string TestDir([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(thisFile)!;

    private static string RepoRoot() => Path.GetFullPath(Path.Combine(TestDir(), "..", ".."));

    /// <summary>Extracts a single synthetic module out of a temporary directory.</summary>
    private static DocModule Extract(string source)
    {
        var root = Directory.CreateTempSubdirectory("docgen-test");
        try
        {
            var stdlib = Directory.CreateDirectory(Path.Combine(root.FullName, "stdlib"));
            File.WriteAllText(Path.Combine(stdlib.FullName, "m.lyr"), source);
            var model = StdlibExtractor.Extract(stdlib.FullName, root.FullName);
            return Assert.Single(model.Modules);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static DocItem Only(string source) => Assert.Single(Extract(source).Items);

    // ------------------------------------------------------------------ the public surface

    [Theory]
    [InlineData("pub fn f(): void { }", ItemKind.Function, "f")]
    [InlineData("pub struct S { x: int, }", ItemKind.Struct, "S")]
    [InlineData("pub class C { x: int, }", ItemKind.Class, "C")]
    [InlineData("pub enum E { A; }", ItemKind.Enum, "E")]
    [InlineData("pub interface I { fn f(): void; }", ItemKind.Interface, "I")]
    [InlineData("pub let x: int = 1;", ItemKind.Binding, "x")]
    [InlineData("pub type Id = int;", ItemKind.Alias, "Id")]
    public void A_public_declaration_arrives(string source, ItemKind kind, string name)
    {
        var item = Only("module m;\n" + source);
        Assert.Equal(kind, item.Kind);
        Assert.Equal(name, item.Name);
    }

    [Theory]
    [InlineData("fn f(): void { }")]
    [InlineData("struct S { x: int, }")]
    [InlineData("class C { x: int, }")]
    [InlineData("enum E { A; }")]
    [InlineData("interface I { fn f(): void; }")]
    [InlineData("let x: int = 1;")]
    [InlineData("type Id = int;")]
    public void A_declaration_without_pub_stays_out(string source)
    {
        Assert.Empty(Extract("module m;\n" + source).Items);
    }

    [Fact]
    public void An_import_is_no_item()
    {
        // What a module uses is its business, not its contract.
        Assert.Empty(Extract("module m;\nimport std.io.console { println };").Items);
    }

    [Fact]
    public void An_extend_is_named_after_its_target()
    {
        var item = Only("module m;\npub extend int :: [Eq] { fn f(): void { } }");
        Assert.Equal(ItemKind.Extend, item.Kind);
        Assert.Equal("int", item.Name);
        Assert.Equal("pub extend int :: [Eq]", item.Signature);
    }

    // ------------------------------------------------------------------ members

    [Fact]
    public void A_private_method_stays_out_but_its_public_neighbour_arrives()
    {
        var c = Only("module m;\npub class C {\n  pub fn shown(): void { }\n  fn hidden(): void { }\n}");
        var member = Assert.Single(c.Members);
        Assert.Equal("shown", member.Name);
        Assert.Equal(ItemKind.Method, member.Kind);
    }

    [Fact]
    public void A_field_arrives_because_it_carries_no_visibility()
    {
        var s = Only("module m;\npub struct S { x: int, y: ?string, }");
        Assert.Equal(["x", "y"], s.Members.Select(m => m.Name));
        Assert.Equal("y: ?string", s.Members[1].Signature);
    }

    [Fact]
    public void Every_interface_member_arrives_because_all_of_them_are_the_contract()
    {
        // No 'pub' inside an interface body, and filtering by it would empty the page.
        var i = Only("module m;\npub interface I {\n  fn a(): void;\n  mut fn b(): int;\n}");
        Assert.Equal(["a", "b"], i.Members.Select(m => m.Name));
    }

    [Fact]
    public void An_enum_lists_its_variants_before_its_methods()
    {
        var e = Only("module m;\npub enum E {\n  None,\n  Some(int);\n  fn tag(): int { return 0; }\n}");
        Assert.Equal([ItemKind.Variant, ItemKind.Variant, ItemKind.Method],
            e.Members.Select(m => m.Kind));
        Assert.Equal(["None", "Some", "tag"], e.Members.Select(m => m.Name));
    }

    // ------------------------------------------------------------------ docs and provenance

    [Fact]
    public void The_doc_block_travels_with_the_item()
    {
        var item = Only("module m;\n/// What it does.\npub fn f(): void { }");
        Assert.Equal("What it does.", item.Doc);
    }

    [Fact]
    public void A_member_carries_its_own_doc_block()
    {
        var c = Only("module m;\npub class C {\n  /// The field.\n  x: int,\n}");
        Assert.Equal("The field.", Assert.Single(c.Members).Doc);
    }

    [Fact]
    public void The_module_path_comes_from_the_header_not_the_file_name()
    {
        Assert.Equal("std.io.file", Extract("module std.io.file;\npub fn f(): void { }").Path);
    }

    [Fact]
    public void The_source_reference_is_relative_and_uses_forward_slashes()
    {
        var item = Only("module m;\n\npub fn f(): void { }");
        Assert.Equal("stdlib/m.lyr", item.Source.File);
        Assert.Equal(3, item.Source.Line);
    }

    [Fact]
    public void A_file_that_does_not_parse_aborts_rather_than_losing_an_item()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Extract("module m;\npub fn ("));
        Assert.Contains("did not parse", e.Message);
    }

    // ------------------------------------------------------------------ the real standard library

    [Fact]
    public void The_model_of_the_standard_library_matches_the_snapshot()
    {
        var actual = Program.Serialize(
            StdlibExtractor.Extract(Path.Combine(RepoRoot(), "stdlib"), RepoRoot()));
        var snapshot = Path.Combine(TestDir(), "golden", "stdlib.json");

        if (UpdateMode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
            File.WriteAllText(snapshot, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(snapshot),
            $"missing snapshot: {snapshot}\n" +
            "Run once with LYRIC_UPDATE_SNAPSHOTS=1 to generate it, then review and commit.");

        var expected = File.ReadAllText(snapshot, Encoding.UTF8).ReplaceLineEndings("\n");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Two_runs_produce_the_same_model()
    {
        // The file order is fixed explicitly; without that the output would depend on the
        // directory enumeration and a snapshot diff would stop meaning content.
        var first = Program.Serialize(
            StdlibExtractor.Extract(Path.Combine(RepoRoot(), "stdlib"), RepoRoot()));
        var second = Program.Serialize(
            StdlibExtractor.Extract(Path.Combine(RepoRoot(), "stdlib"), RepoRoot()));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Every_standard_library_module_arrives()
    {
        var model = StdlibExtractor.Extract(Path.Combine(RepoRoot(), "stdlib"), RepoRoot());
        var onDisk = Directory
            .GetFiles(Path.Combine(RepoRoot(), "stdlib"), "*.lyr", SearchOption.AllDirectories)
            .Length;

        Assert.Equal(onDisk, model.Modules.Length);
        Assert.All(model.Modules, m => Assert.StartsWith("std.", m.Path));
        Assert.All(model.Modules, m => Assert.NotEmpty(m.Items));
    }
}
