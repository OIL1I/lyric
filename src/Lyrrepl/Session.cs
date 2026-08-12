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
/// The state of a REPL session and the rule that turns an entry into a program.
///
/// <para>Declarations accumulate, statements run once. <c>fn</c>, <c>class</c>, <c>struct</c>,
/// <c>enum</c> and module-level <c>let</c> go into a growing preamble; everything else becomes
/// the body of a synthetic <c>main</c> and is forgotten after the run.</para>
///
/// <para>The initializer of a declaration therefore runs again on every entry.</para>
/// </summary>
public sealed class Session(string? stdlibRoot)
{
    /// <summary>What has been declared so far, in entry order: the preamble of every
    /// program.</summary>
    private readonly List<string> _declarations = new();

    /// <summary>How many entries have been compiled. Used for the file name in diagnostics, so
    /// that "line 3" refers to the third entry.</summary>
    private int _entries;

    public IReadOnlyList<string> Declarations => _declarations;

    /// <summary>
    /// Is this entry a declaration (kept) or a statement (run once)?
    ///
    /// <para>Decided from the first token, before anything is compiled.</para>
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
    /// Builds the program for this entry: every declaration so far, plus the entry itself either
    /// as another declaration or as the body of <c>main</c>.
    ///
    /// <para>An expression is printed, a statement only executed. An expression is one that does
    /// not end in <c>;</c> and is not a block.</para>
    /// </summary>
    public string Program(string input, bool printed = true)
    {
        var source = new StringBuilder();

        // 'console' is imported into every entry; printing an expression needs it. An unused
        // import costs nothing: the import table only carries what is actually called.
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
    /// Appends a <c>;</c> where the grammar requires one and the entry has none.
    ///
    /// <para>Affects the terminated declarations (<c>let</c>, <c>import</c>, <c>module</c>);
    /// <c>fn</c> and <c>class</c> end in <c>}</c> and need none.</para>
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

    /// <summary>Wraps an expression in a <c>println</c>; leaves a statement as it is.</summary>
    private static string Statement(string input, bool printed = true)
    {
        var trimmed = input.Trim();

        // Anything ending in ';' or forming a block is a statement, not an expression.
        if (!printed || trimmed.EndsWith(';') || trimmed.EndsWith('}'))
            return "    " + trimmed + (EndsStatement(trimmed) ? "" : ";");

        // Printed through an f-string, which accepts every Display type and formats through the
        // standard library.
        return $"    console.println(f\"{{{trimmed}}}\");";
    }

    /// <summary>
    /// Compiles and runs. Returns <c>true</c> when it ran, which is when a declaration is kept.
    ///
    /// <para>A failing entry leaves the session state unchanged.</para>
    /// </summary>
    public bool Execute(string input, TextWriter output, TextWriter error)
    {
        // Whether a call is printable depends on its TYPE, not its syntax: a call returning
        // 'void' cannot be printed. So two attempts, expression first and statement second; the
        // diagnostics of the first attempt are discarded.
        if (!IsDeclaration(input) && !EndsStatement(input))
        {
            var quiet = new StringWriter();
            if (Attempt(input, printed: true, output, quiet)) return true;
        }

        return Attempt(input, printed: false, output, error);
    }

    /// <summary>Does the entry end in a way that makes it certainly a statement?</summary>
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

        // The try covers the lowering as well as the run: a compiler scope limit throws there
        // (InternalCompilationException), and that must end the entry, not the session.
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
            // A panic ends the entry here, not the session.
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
            // A compiler limit ends the entry, not the session.
            error.WriteLine($"internal: {internalError.Message}");
            return false;
        }

        if (IsDeclaration(input)) _declarations.Add(input);
        return true;
    }

    /// <summary>Forgets every declaration — <c>:reset</c>.</summary>
    public void Reset() => _declarations.Clear();
}
