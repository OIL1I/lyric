using System.Runtime.CompilerServices;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Tests.Vm;

/// <summary>
/// Sema and backend agree: WHAT THE SEMA LETS THROUGH THE BACKEND HAS TO HANDLE.
///
/// <para>For every combination of type and operation there are exactly two allowed outcomes — a
/// diagnostic, or a program that gets through the lowering. A third outcome exists all the same: the
/// <see cref="InternalCompilationException"/>. It means the sema held something valid that the verifier
/// rejects, and the user gets A STACK TRACE INSTEAD OF AN ERROR MESSAGE.</para>
///
/// <para>WHY THIS IS A MATRIX RATHER THAN A LIST OF EXAMPLES. In a single session four such crashes
/// appeared: a module <c>let</c> with a <c>T[]</c>, an f-string with an <c>int8</c>, <c>==</c> on a
/// <c>struct</c>, and <c>string &lt; string</c>. Each was found BY ACCIDENT, while building something
/// else that happened to lie next to it. Four accidents are no accident but a structural gap: the rules
/// stand at two places and nobody ever held them against each other.</para>
///
/// <para>This class holds them against each other. It catches not the KNOWN cases but the CLASS, and a
/// new type or operator is checked here automatically as soon as it stands in the lists.</para>
///
/// <para>WHAT IS NOT CHECKED: whether the sema decides correctly. A diagnostic counts as success here
/// even if it were factually wrong. This test answers exactly one question — "does the compiler
/// crash?" — and tests answering several questions no longer say which one failed when red.</para>
/// </summary>
public class AgreementTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    /// <summary>One value per type: the declaration and the expression yielding it. The name contains the
    /// type, so two values of the same type fit side by side.</summary>
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

    /// <summary>Unary operations. <c>{0}</c> is the value.</summary>
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

    /// <summary>Binary operations over two values of the same type.</summary>
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

        // The actual promise. A diagnostic is fine and so is a compiled program; only a throw is not.
        var exception = Record.Exception(() => Compile(source));

        Assert.True(exception is null,
            $"'{combination}' liess den Compiler werfen statt zu diagnostizieren:\n" +
            $"{exception?.GetType().Name}: {exception?.Message}\n\n{source}");
    }

    private static string Body(string declarations, string statement) =>
        Prelude + "\nfn main(): int {\n    " + declarations + "\n    " + statement
        + "\n    return 0;\n}\n";

    /// <summary>The same declaration a second time, under a different name.</summary>
    private static string Renamed(string declaration, string name, string prefix = "w_") =>
        declaration.Replace("v_" + name, prefix + name);

    /// <summary>
    /// Compiles to the end of the lowering, the verifier included, because that is exactly where the
    /// disagreement strikes.
    /// </summary>
    /// <remarks>NOT executed: a <c>panic</c> at runtime is a valid outcome — an index out of range, a char
    /// outside its range — and says nothing here.</remarks>
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

        // A diagnostic is a valid outcome; then there is nothing to lower.
        if (diagnostics.HasErrors) return;

        ModuleLowerer.Lower(compilation, binding, types, diagnostics, verify: true);
    }
}
