using System.Runtime.CompilerServices;
using System.Text;
using Lyric.Core;
using Lyric.Parsing;
using Lyric.Resolver;
using Xunit;

namespace Lyric.Tests.Resolver;

/// <summary>
/// Golden tests for the resolver. Every fixture (golden/&lt;name&gt;.lyr) is a single module that is
/// parsed and resolved; the symbol dump, plus rendered diagnostics, is compared against the committed
/// snapshot (golden/&lt;name&gt;.symbols).
///
/// Snapshots as for the parser: produce them once with LYRIC_UPDATE_SNAPSHOTS=1, read them over, commit.
/// The fixtures parse cleanly on purpose, so only resolver diagnostics appear.
/// </summary>
public class GoldenTests
{
    private static bool UpdateMode =>
        Environment.GetEnvironmentVariable("LYRIC_UPDATE_SNAPSHOTS") is "1" or "true";

    private static string GoldenDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "golden");

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string ResolveAndDump(string name, string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual(name + ".lyr", source);
        var de = new DiagnosticEngine(sm);
        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        comp.Resolve();

        var dump = Normalize(SymbolDumper.Dump(comp));
        if (!dump.EndsWith('\n')) dump += "\n";

        if (de.Count == 0) return dump;

        var sw = new StringWriter(new StringBuilder()) { NewLine = "\n" };
        de.RenderText(sw);
        return dump + "\n=== diagnostics ===\n" + Normalize(sw.ToString());
    }

    [Theory]
    [InlineData("basic_module")]    // struct + Methode, pub fn, global let, type-Alias
    [InlineData("imports")]         // three import forms, external because this is a single file
    [InlineData("enum_interface")]  // enum variants and methods, interface members
    [InlineData("visibility")]      // pub vs. modul-privat
    [InlineData("duplicate_decl")]  // Duplikat (LYR-RES0001)
    [InlineData("unresolved_type")] // unbekannter Typ (LYR-RES0002)
    public void Golden_symbols_match_snapshot(string name)
    {
        var dir = GoldenDir();
        var inputPath = Path.Combine(dir, name + ".lyr");
        var snapshotPath = Path.Combine(dir, name + ".symbols");

        Assert.True(File.Exists(inputPath), $"missing fixture: {inputPath}");

        var source = File.ReadAllText(inputPath, Encoding.UTF8);
        var actual = ResolveAndDump(name, source);

        if (UpdateMode)
        {
            File.WriteAllText(snapshotPath, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(snapshotPath),
            $"missing snapshot: {snapshotPath}\n" +
            "Run once with LYRIC_UPDATE_SNAPSHOTS=1 to generate it, then review and commit.");

        var expected = Normalize(File.ReadAllText(snapshotPath, Encoding.UTF8));
        Assert.Equal(expected, actual);
    }
}
