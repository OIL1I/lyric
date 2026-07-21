using System.Runtime.CompilerServices;
using System.Text;
using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Xunit;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Golden-Tests für den Parser (Slice 1: Expressions + TypeExpr in Casts).
/// Jede Fixture (golden/&lt;name&gt;.lyr) enthält GENAU EINEN Ausdruck. Sie wird
/// geparst, der AST-Dump (+ gerenderte Diagnostics bei Negativ-Fällen) gegen den
/// committeten Snapshot (golden/&lt;name&gt;.ast) verglichen.
///
/// Snapshots werden NICHT von Hand gepflegt: einmal mit Env-Var
/// LYRIC_UPDATE_SNAPSHOTS=1 erzeugen, drüberlesen, committen. Danach lockt der
/// Vergleich den AST fest.
///
/// ERWARTETER PARSER-KONTRAKT (Slice 1, von dir zu implementieren):
///   - new Parser(SourceManager sm, FileId id, DiagnosticEngine de)
///   - Expr Parser.ParseExpression()
///   - static string AstDumper.Dump(Node node, SourceManager sm)
/// Bis diese Typen existieren, ist dieses Test-Projekt absichtlich ROT (TDD).
/// </summary>
public class GoldenTests
{
    private static bool UpdateMode =>
        Environment.GetEnvironmentVariable("LYRIC_UPDATE_SNAPSHOTS") is "1" or "true";

    // [CallerFilePath] liefert den Pfad dieser Datei zur Compile-Zeit → Snapshots werden
    // im Source-Baum gelesen/geschrieben, nicht im bin/-Output.
    private static string GoldenDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "golden");

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static (DiagnosticEngine diag, string dump) ParseExprAndDump(string displayName, string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual(displayName, source);
        var de = new DiagnosticEngine(sm);
        var parser = new Parser(sm, id, de);

        var expr = parser.ParseExpression();

        var dump = Normalize(AstDumper.Dump(expr, sm));
        if (!dump.EndsWith('\n')) dump += "\n";

        if (de.Count == 0) return (de, dump);

        var sw = new StringWriter(new StringBuilder()) { NewLine = "\n" };
        de.RenderText(sw);
        return (de, dump + "\n=== diagnostics ===\n" + Normalize(sw.ToString()));
    }

    [Theory]
    // Positiv — kein Diagnostic, nur AST-Dump.
    [InlineData("precedence")]        // arithmetische Präzedenz + Linksassoziativität
    [InlineData("prefix_postfix")]    // prefix !/-/~/--  vs. postfix ++/!(unwrap)
    [InlineData("assignment")]        // Rechtsassoziativität + Compound-Assign
    [InlineData("logical_comparison")]// lange Präzedenz-Kette < == && || ??
    [InlineData("bitwise_shift")]     // << >> & ^ | Präzedenz
    [InlineData("range")]             // ..= gegen + Präzedenz
    [InlineData("coalesce")]          // ?? Rechtsassoziativität
    [InlineData("cast")]              // 'as' + TypeExpr, Linksassoziativität
    [InlineData("postfix_chain")]     // . ?. ( ) [ ] Kette
    [InlineData("literals")]          // alle Literal-Klassen in einem Array-Lit
    [InlineData("fstring")]           // InterpolatedStringExpr mit Loch + FormatSpec
    [InlineData("fstring_plain")]     // f-String ohne Interpolation
    [InlineData("array_tuple")]       // Array-Lit + Tuple-Lit verschachtelt
    [InlineData("tuple_big")]         // Tuple mit 5 Elementen (keine Arity-Obergrenze)
    [InlineData("empty_array")]       // []  (leeres Array-Literal)
    [InlineData("lambda")]            // LambdaExpr mit Param-Typ-Annotation
    [InlineData("nested_lambda")]     // rechts-verschachteltes Lambda
    [InlineData("grouping")]          // Klammer-Gruppierung überschreibt Präzedenz
    [InlineData("atident")]           // AtIdentifierExpr mit Argumenten
    // TypeExpr (§4) — via 'as'-Cast erreicht.
    [InlineData("type_generics")]     // NamedType mit Typargumenten
    [InlineData("type_nested_generics")] // '>>'-Split bei verschachtelten Generics
    [InlineData("type_function")]     // FunctionType fn(...) -> R
    [InlineData("type_array")]        // ArrayType T[N]
    [InlineData("type_nullable")]     // NullableType ?T
    [InlineData("type_tuple")]        // TupleType (A, B, C)
    // Negativ — Snapshot enthält AST-Dump (ggf. ErrorExpr) UND gerenderte Diagnostics.
    [InlineData("unclosed_paren")]    // (1 + 2
    [InlineData("missing_rhs")]       // 1 +
    [InlineData("leading_operator")]  // * 3
    [InlineData("type_error")]        // x as 5  (Nicht-Typ nach 'as')
    public void Golden_fixture_matches_snapshot(string name)
    {
        var dir = GoldenDir();
        var inputPath = Path.Combine(dir, name + ".lyr");
        var snapshotPath = Path.Combine(dir, name + ".ast");

        Assert.True(File.Exists(inputPath), $"missing fixture: {inputPath}");

        var source = File.ReadAllText(inputPath, Encoding.UTF8);
        var (_, actual) = ParseExprAndDump(name + ".lyr", source);

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
