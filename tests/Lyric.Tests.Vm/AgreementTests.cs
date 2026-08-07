using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Vm;

/// <summary>
/// Sema und Backend sind sich einig: <b>was die Sema durchlässt, muss das Backend können</b>.
///
/// <para>Für jede Kombination aus Typ und Operation gibt es genau zwei erlaubte Ausgänge — eine
/// Diagnose, oder ein Programm, das durchs Lowering kommt. Ein dritter Ausgang existiert heute
/// trotzdem: die <see cref="InternalCompilationException"/>. Sie bedeutet, dass die Sema etwas
/// für gültig hielt, das der Verifier ablehnt — und der Nutzer bekommt einen <b>Stack-Trace statt
/// einer Fehlermeldung</b>.</para>
///
/// <para><b>Warum das eine Matrix ist und keine Liste von Beispielen.</b> In einer einzigen
/// Sitzung (2026-08-07) sind vier solche Abstürze aufgetaucht: ein Modul-<c>let</c> mit
/// <c>T[]</c>, ein f-String mit <c>int8</c>, <c>==</c> auf einem <c>struct</c> und
/// <c>string &lt; string</c>. Gefunden wurde jeder davon durch <b>Zufall</b> — beim Bauen von
/// etwas anderem, das zufällig danebenlag. Vier Zufälle sind kein Zufall mehr, sondern eine
/// strukturelle Lücke: die Regeln stehen an zwei Stellen, und niemand hat sie je gegeneinander
/// gehalten.</para>
///
/// <para>Diese Klasse hält sie gegeneinander. Sie fängt keine <i>bekannten</i> Fälle, sondern die
/// <i>Klasse</i> — und ein neuer Typ oder Operator wird hier automatisch mitgeprüft, sobald er in
/// den Listen steht.</para>
///
/// <para><b>Was NICHT geprüft wird</b>: ob die Sema richtig entscheidet. Eine Diagnose gilt hier
/// als Erfolg, auch wenn sie sachlich falsch wäre. Dieser Test beantwortet genau eine Frage —
/// „stürzt der Compiler ab?" — und Tests, die mehrere Fragen beantworten, sagen bei Rot nicht
/// mehr, welche.</para>
/// </summary>
public class AgreementTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>Ein Wert je Typ: die Deklaration und der Ausdruck, der ihn liefert. Der Name
    /// enthaelt den Typ, damit zwei Werte desselben Typs nebeneinander passen.</summary>
    private static readonly (string Name, string Declaration, string Expression)[] Values =
    [
        ("int",     "let v_int: int = 1;",          "v_int"),
        ("int8",    "let v_int8: int8 = 1;",        "v_int8"),
        ("uint",    "let v_uint: uint = 1;",        "v_uint"),
        ("uint8",   "let v_uint8: uint8 = 1;",      "v_uint8"),
        ("float",   "let v_float: float = 1.0;",    "v_float"),
        ("float32", "let v_float32: float32 = 1.0;", "v_float32"),
        ("bool",    "let v_bool = true;",           "v_bool"),
        ("char",    "let v_char = 'a';",            "v_char"),
        ("string",  "let v_string = \"s\";",        "v_string"),
        ("array",   "let v_array = [1, 2];",        "v_array"),
        ("tuple",   "let v_tuple = (1, 2);",        "v_tuple"),
        ("opt",     "let v_opt: ?int = 1;",         "v_opt"),
        ("struct",  "let v_struct = S { a = 1, b = 2 };", "v_struct"),
        ("class",   "let v_class = K { v = 1 };",   "v_class"),
        ("enum",    "let v_enum = E.One;",          "v_enum"),
        ("fn",      "let v_fn = (n: int) => n;",    "v_fn"),
    ];

    /// <summary>Einstellige Operationen. <c>{0}</c> ist der Wert.</summary>
    private static readonly (string Name, string Template)[] Unary =
    [
        ("interpolate", "println(f\"{{{0}}}\");"),
        ("spec",        "println(f\"{{{0}:N2}}\");"),
        ("negate",      "let r = -({0});"),
        ("bitnot",      "let r = ~({0});"),
        ("not",         "let r = !({0});"),
        ("index",       "let r = ({0})[0];"),
        ("castInt",     "let r = ({0}) as int;"),
        ("castFloat",   "let r = ({0}) as float;"),
        ("castChar",    "let r = ({0}) as char;"),
        ("force",       "let r = ({0})!;"),
        ("call",        "let r = ({0})();"),
        ("member",      "let r = ({0}).a;"),
        ("iterate",     "for (e in {0}) {{ }}"),
        ("coalesceNull", "let r = ({0}) ?? ({0});"),
    ];

    /// <summary>Zweistellige Operationen über zwei Werten desselben Typs.</summary>
    private static readonly (string Name, string Template)[] Binary =
    [
        ("add", "let r = ({0}) + ({1});"),
        ("sub", "let r = ({0}) - ({1});"),
        ("mul", "let r = ({0}) * ({1});"),
        ("div", "let r = ({0}) / ({1});"),
        ("rem", "let r = ({0}) % ({1});"),
        ("eq",  "let r = ({0}) == ({1});"),
        ("ne",  "let r = ({0}) != ({1});"),
        ("lt",  "let r = ({0}) < ({1});"),
        ("ge",  "let r = ({0}) >= ({1});"),
        ("and", "let r = ({0}) && ({1});"),
        ("bitAnd", "let r = ({0}) & ({1});"),
        ("bitOr",  "let r = ({0}) | ({1});"),
        ("shl", "let r = ({0}) << ({1});"),
    ];

    private const string Prelude = """
        import std.io.console { println };

        pub struct S { a: int, b: int }
        pub class K { v: int }
        pub enum E { One, Two }

        """;

    public static TheoryData<string> EveryCombination
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var value in Values)
            {
                foreach (var op in Unary)
                    data.Add($"u|{value.Name}|{op.Name}");
                foreach (var op in Binary)
                    data.Add($"b|{value.Name}|{op.Name}");
                data.Add($"g|{value.Name}|global");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public void Neither_a_crash_nor_a_silent_pass(string combination)
    {
        var parts = combination.Split('|');
        var value = Values.First(v => v.Name == parts[1]);
        var source = parts[0] switch
        {
            "u" => Body(value.Declaration,
                     string.Format(Unary.First(o => o.Name == parts[2]).Template, value.Expression)),
            "b" => Body(value.Declaration + "\n    " + Renamed(value.Declaration, value.Name),
                     string.Format(Binary.First(o => o.Name == parts[2]).Template,
                                   value.Expression, "w_" + value.Name)),
            _   => Prelude + "\n" + Renamed(value.Declaration, value.Name, "g_")
                   + "\nfn main(): int { return 0; }\n",
        };

        // Die eigentliche Zusicherung. Eine Diagnose ist in Ordnung, ein uebersetztes Programm
        // auch — nur ein Wurf ist es nicht.
        var exception = Record.Exception(() => Compile(source));

        Assert.True(exception is null,
            $"'{combination}' liess den Compiler werfen statt zu diagnostizieren:\n" +
            $"{exception?.GetType().Name}: {exception?.Message}\n\n{source}");
    }

    private static string Body(string declarations, string statement) =>
        Prelude + "\nfn main(): int {\n    " + declarations + "\n    " + statement
        + "\n    return 0;\n}\n";

    /// <summary>Dieselbe Deklaration ein zweites Mal, unter anderem Namen.</summary>
    private static string Renamed(string declaration, string name, string prefix = "w_") =>
        declaration.Replace("v_" + name, prefix + name);

    /// <summary>
    /// Uebersetzt bis zum Ende des Lowerings — inklusive Verifier, denn genau dort schlaegt die
    /// Uneinigkeit zu.
    /// </summary>
    /// <remarks>Wird NICHT ausgefuehrt: ein <c>panic</c> zur Laufzeit ist ein gueltiger Ausgang
    /// (ein Index daneben, ein char ausserhalb des Bereichs) und hier keine Aussage.</remarks>
    private static void Compile(string source)
    {
        var sources = new SourceManager();
        var file = sources.AddVirtual("agreement.lyr", source);
        var diagnostics = new DiagnosticEngine(sources);

        var compilation = new Compilation(sources, diagnostics)
        {
            ModuleLoader = StdlibLoader.ForRoot(
                Path.Combine(RepoRoot(), "stdlib"), sources, diagnostics),
        };
        compilation.AddModule(new Parser(sources, file, diagnostics).ParseModule());

        var binding = compilation.Resolve();
        var types = Semantics.Analyze(compilation, binding, diagnostics);

        // Eine Diagnose ist ein gueltiger Ausgang — dann gibt es nichts zu lowern.
        if (diagnostics.HasErrors) return;

        ModuleLowerer.Lower(compilation, binding, types, diagnostics, verify: true);
    }
}
