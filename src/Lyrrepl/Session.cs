using System.Text;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir.Lowering;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Cli.Repl;

/// <summary>
/// Der Zustand einer REPL-Sitzung und die Regel, wie eine Eingabe zu einem Programm wird.
///
/// <para><b>Deklarationen sammeln sich an, Statements laufen einmal.</b> Das ist die ganze
/// Mechanik, und sie löst das Problem, an dem eine naive REPL scheitert: wer schlicht den
/// Quelltext akkumuliert und alles neu übersetzt, lässt jedes <c>println</c> bei jeder folgenden
/// Eingabe erneut laufen. Hier wandern <c>fn</c>, <c>class</c>, <c>struct</c>, <c>enum</c> und
/// Modul-<c>let</c> in einen wachsenden Vorspann; alles andere wird der Rumpf eines
/// synthetischen <c>main</c> und ist nach dem Lauf vergessen.</para>
///
/// <para><b>Der Preis, ausgesprochen</b> (ADR-021): der <i>Initialisierer</i> einer Deklaration
/// läuft bei jeder Eingabe neu. Bei <c>let x = 5</c> ist das unsichtbar, bei
/// <c>let s = readText(…)</c> nicht. Ein Wert, der wirklich einmal berechnet wird, bräuchte
/// persistente Globals in der VM — formatneutral nachrüstbar, und diese Trennung bliebe dabei
/// unverändert.</para>
/// </summary>
public sealed class Session(string? stdlibRoot)
{
    /// <summary>Was bisher deklariert wurde, in Eingabereihenfolge. Der Vorspann jedes
    /// Programms.</summary>
    private readonly List<string> _declarations = new();

    /// <summary>Wie viele Eingaben bisher übersetzt wurden — nur für die Dateinamen in
    /// Diagnosen, damit „line 3" die dritte Eingabe meint und nicht die dritte Zeile.</summary>
    private int _entries;

    public IReadOnlyList<string> Declarations => _declarations;

    /// <summary>
    /// Ist diese Eingabe eine Deklaration (bleibt) oder ein Statement (läuft einmal)?
    ///
    /// <para>Entschieden wird am <b>ersten Token</b> und nicht durch einen Parse-Versuch: die
    /// Antwort muss feststehen, <i>bevor</i> irgendetwas übersetzt wird, und ein fehlgeschlagener
    /// Versuch hinterließe Diagnosen, die niemand sehen soll.</para>
    /// </summary>
    public static bool IsDeclaration(string input)
    {
        var trimmed = input.TrimStart();

        foreach (var keyword in new[] { "fn ", "class ", "struct ", "enum ", "interface ",
                                        "extend ", "import ", "module ", "pub ", "let " })
            if (trimmed.StartsWith(keyword, StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>
    /// Baut das Programm für diese Eingabe: alle bisherigen Deklarationen plus, je nach Art der
    /// Eingabe, sie selbst als weitere Deklaration oder als Rumpf von <c>main</c>.
    ///
    /// <para>Ein <b>Ausdruck</b> wird gedruckt, ein Statement nur ausgeführt — das ist das, was
    /// eine REPL von einem Skript unterscheidet. Woran man beides erkennt: ein Ausdruck endet
    /// nicht auf <c>;</c> und ist kein Block.</para>
    /// </summary>
    public string Program(string input, bool printed = true)
    {
        var source = new StringBuilder();

        // 'console' ist in jeder Eingabe da. Wer an einer REPL sitzt, will einen Wert sehen und
        // nicht erst einen Import tippen — und das Drucken eines Ausdrucks (unten) braucht ihn
        // ohnehin. Ein ungenutzter Import kostet nichts: die Import-Tabelle traegt nur, was
        // wirklich gerufen wird.
        source.Append("import std.io.console;\n");

        foreach (var declaration in _declarations)
            source.Append(Terminated(declaration)).Append('\n');

        if (IsDeclaration(input))
        {
            source.Append(Terminated(input)).Append('\n');
            source.Append("fn main(): int { return 0; }\n");
            return source.ToString();
        }

        source.Append("fn main(): int {\n");
        source.Append(Statement(input, printed)).Append('\n');
        source.Append("    return 0;\n}\n");
        return source.ToString();
    }

    /// <summary>
    /// Hängt ein <c>;</c> an, wo die Grammatik eines verlangt und der Nutzer keines getippt hat.
    ///
    /// <para>An einer REPL schreibt niemand <c>let x = 5;</c> — das Semikolon ist dort ein Ritual
    /// ohne Zweck, weil die Zeile ohnehin endet. Betroffen sind nur die Deklarationen mit
    /// Abschluss (<c>let</c>, <c>import</c>, <c>module</c>); <c>fn</c> und <c>class</c> enden auf
    /// <c>}</c> und brauchen keines.</para>
    /// </summary>
    private static string Terminated(string declaration)
    {
        var trimmed = declaration.TrimEnd();
        if (trimmed.EndsWith(';') || trimmed.EndsWith('}')) return trimmed;

        var head = trimmed.TrimStart();
        foreach (var keyword in new[] { "let ", "pub let ", "import ", "module " })
            if (head.StartsWith(keyword, StringComparison.Ordinal))
                return trimmed + ";";

        return trimmed;
    }

    /// <summary>Ein Ausdruck bekommt ein <c>println</c> um sich; ein Statement bleibt, wie es
    /// ist.</summary>
    private static string Statement(string input, bool printed = true)
    {
        var trimmed = input.Trim();

        // Alles, was mit ';' endet oder ein Block ist, ist ein Statement — es zu drucken hiesse,
        // 'x = 5;' als Ausdruck zu lesen, und das ist es in Lyric nicht (§6.1).
        if (!printed || trimmed.EndsWith(';') || trimmed.EndsWith('}'))
            return "    " + trimmed + (EndsStatement(trimmed) ? "" : ";");

        // Ein Ausdruck: gedruckt ueber einen f-String, weil der jeden Display-faehigen Typ nimmt
        // und die Formatierung der Stdlib ueberlaesst.
        return $"    console.println(f\"{{{trimmed}}}\");";
    }

    /// <summary>
    /// Übersetzt und führt aus. Liefert <c>true</c>, wenn es lief — dann wird eine Deklaration
    /// behalten.
    ///
    /// <para>Eine fehlerhafte Eingabe ändert den Zustand <b>nicht</b>. Das ist die wichtigste
    /// Eigenschaft einer REPL-Sitzung: wer sich vertippt, sitzt danach nicht auf einem Vorspann,
    /// der nicht mehr übersetzt.</para>
    /// </summary>
    public bool Execute(string input, TextWriter output, TextWriter error)
    {
        // Ein Ausdruck wird gedruckt, ein Statement nur ausgefuehrt — aber ob 'console.println(x)'
        // das eine oder das andere ist, entscheidet der TYP und nicht die Syntax: ein Aufruf, der
        // 'void' liefert, laesst sich nicht drucken.
        //
        // Deshalb zwei Versuche: erst als Ausdruck, und wenn das scheitert, als Statement. Die
        // Diagnosen des ersten Versuchs werden VERWORFEN — der Nutzer soll nicht lesen, was der
        // Interpreter zuerst vermutet hat.
        if (!IsDeclaration(input) && !EndsStatement(input))
        {
            var quiet = new StringWriter();
            if (Attempt(input, printed: true, output, quiet)) return true;
        }

        return Attempt(input, printed: false, output, error);
    }

    /// <summary>Endet die Eingabe so, dass sie sicher ein Statement ist?</summary>
    private static bool EndsStatement(string input)
    {
        var trimmed = input.TrimEnd();
        return trimmed.EndsWith(';') || trimmed.EndsWith('}');
    }

    private bool Attempt(string input, bool printed, TextWriter output, TextWriter error)
    {
        _entries++;

        var sources = new SourceManager();
        var file = sources.AddVirtual($"repl[{_entries}].lyr", Program(input, printed));
        var diagnostics = new DiagnosticEngine(sources);

        var compilation = new Compilation(sources, diagnostics);
        if (stdlibRoot is not null)
            compilation.ModuleLoader = StdlibLoader.ForRoot(stdlibRoot, sources, diagnostics);

        compilation.AddModule(new Parser(sources, file, diagnostics).ParseModule());

        var binding = compilation.Resolve();
        var types = Semantics.Analyze(compilation, binding, diagnostics);

        if (diagnostics.HasErrors)
        {
            diagnostics.RenderText(error);
            return false;
        }

        // Der try umfasst das LOWERING mit, nicht nur den Lauf: eine Scope-Grenze des Compilers
        // wirft dort (InternalCompilationException), und interaktiv darf das die Eingabe beenden
        // und nicht die Sitzung. Beim ersten Versuch stand er nur um den Interpreter — und ein
        // 'let xs = [1, 2]' riss die ganze REPL mit.
        try
        {
            var ir = ModuleLowerer.Lower(compilation, binding, types, diagnostics);
            if (ir is null)
            {
                diagnostics.RenderText(error);
                return false;
            }

            var module = BytecodeReader.ReadOrThrow(BytecodeWriter.Write(ir));
            Interpreter.Run(module, [], NativeRegistry.CreateDefault(output, error));
        }
        catch (LyricPanic panic)
        {
            // Ein panic beendet in einem Programm die VM (§9). In einer REPL beendet er die
            // EINGABE — die Sitzung laeuft weiter, sonst waere jeder Tippfehler das Ende.
            error.WriteLine($"panic [{panic.Code}]: {panic.Message}");
            foreach (var frame in panic.CallStack) error.WriteLine($"    in {frame}");
            return false;
        }
        catch (LyricRuntimeException runtime)
        {
            error.WriteLine($"error[{runtime.Code}]: {runtime.Message}");
            return false;
        }
        catch (InternalCompilationException internalError)
        {
            // Eine Grenze des Compilers beendet die EINGABE, nicht die Sitzung. In einem Programm
            // waere ein solcher Wurf ein Absturz mit Stack-Trace; interaktiv ist er eine Zeile,
            // die nicht ging — und der Nutzer tippt weiter.
            error.WriteLine($"internal: {internalError.Message}");
            return false;
        }

        if (IsDeclaration(input)) _declarations.Add(input);
        return true;
    }

    /// <summary>Vergisst alle Deklarationen — <c>:reset</c>.</summary>
    public void Reset() => _declarations.Clear();
}
