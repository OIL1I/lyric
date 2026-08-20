using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Ir;

/// <summary>
/// The pub-roots rule (§4.6 of the specification, since 2.0): a compile WITHOUT an entry point —
/// a library — takes the `pub` functions of its compiled modules as reachability roots, so a
/// library's surface decides its contents.
///
/// <para>The rule rides an OPT-IN of the drivers (<c>libraryRoots: true</c> through
/// <c>SourceCompiler</c>): a test lowering a bare snippet through the raw API keeps every function
/// it wrote, which the last test here pins — hundreds of existing fixtures depend on it.</para>
/// </summary>
public class ExportRootTests
{
    private static IrModule Lower(string source, bool libraryRoots)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("lib.lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);
        Assert.False(de.HasErrors);

        // optimize:false — these tests pin WHICH functions survive, and the inliner folding a
        // single-caller helper into its caller would blur exactly that.
        var ir = ModuleLowerer.Lower(comp, binding, types, de, verify: true, optimize: false,
            libraryRoots: libraryRoots);
        Assert.NotNull(ir);
        return ir!;
    }

    private const string Library = """
        pub fn surface(): int { return helper(); }
        fn helper(): int { return 7; }
        fn orphan(): int { return 8; }
        """;

    [Fact]
    public void A_library_prunes_from_its_pub_surface()
    {
        var module = Lower(Library, libraryRoots: true);

        // 'surface' is the surface, 'helper' is reachable through it — 'orphan' is neither and
        // does not ship.
        Assert.Contains(module.Functions, f => f.Name.EndsWith(".surface"));
        Assert.Contains(module.Functions, f => f.Name.EndsWith(".helper"));
        Assert.DoesNotContain(module.Functions, f => f.Name.EndsWith(".orphan"));
    }

    [Fact]
    public void The_export_roots_follow_the_renumbering()
    {
        var module = Lower(Library, libraryRoots: true);

        // After the prune the recorded roots must still point at the pub functions — an index
        // into the pre-prune numbering would name an arbitrary survivor.
        var root = Assert.Single(module.ExportRoots);
        Assert.EndsWith(".surface", module.Functions[root.Value].Name);
    }

    [Fact]
    public void A_bare_snippet_through_the_raw_api_keeps_everything()
    {
        var module = Lower(Library, libraryRoots: false);

        // The pre-2.0 behavior, deliberately kept for the raw API: no entry point and no export
        // roots means no pruning at all.
        Assert.Contains(module.Functions, f => f.Name.EndsWith(".orphan"));
    }
}
