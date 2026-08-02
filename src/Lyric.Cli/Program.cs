using Lyric.AST;
using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Ir;
using Lyric.Ir.Lowering;
using Lyric.Lexing;
using Lyric.Parsing;
using Lyric.Resolver;
using Lyric.Sema;
using Lyric.Vm;

namespace Lyric.Cli;

public static class Program
{
    // Update this on each release tag.
    private const string Version = "0.0.1-dev";

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        return args[0] switch
        {
            "--version" or "-v" => PrintVersion(),
            "--help" or "-h" => HelpAndOk(),
            "tokenize" => Tokenize(args),
            "parse" => Parse(args),
            "check" => CheckCmd(args),
            "lower" => Lower(args),
            "build" => Build(args),
            "disasm" => Disasm(args),
            "run" => RunCmd(args),
            _ => Unknown(args[0]),
        };
    }

    /// <summary>Compiliert und führt aus. Nimmt <c>.lyr</c> (compiliert im Speicher) oder
    /// <c>.lyrbc</c> (lädt direkt). Der Rückgabewert von <c>main</c> ist der Prozess-Exit-Code
    /// (Sprache.md §11).</summary>
    private static int RunCmd(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("run: missing file argument");
            return 2;
        }
        var fpath = args[1];
        var de = new DiagnosticEngine(new SourceManager());

        byte[] bytes;
        if (Path.GetExtension(fpath).Equals(".lyrbc", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                bytes = File.ReadAllBytes(fpath);
            }
            catch
            {
                Console.Error.WriteLine($"run: failed to read file: {fpath}");
                return 1;
            }
        }
        else
        {
            var pipeline = Compile(fpath);
            if (pipeline.Ir is null) return 1;
            bytes = BytecodeWriter.Write(pipeline.Ir);
        }

        var module = BytecodeReader.Read(bytes, de);
        de.RenderText(Console.Error);
        if (module is null) return 1;

        try
        {
            // §11: Exit-Code ist 0..255. Wie jedes POSIX-System nehmen wir das niedrigste Byte.
            return (int)(Interpreter.Run(module).AsI64 & 0xFF);
        }
        catch (LyricPanic panic)
        {
            // §9: ein panic druckt einen Backtrace und beendet die VM. Nicht catchbar.
            Console.Error.WriteLine($"panic [{panic.Code}]: {panic.Message}");
            foreach (var frame in panic.CallStack) Console.Error.WriteLine($"    in {frame}");

            // 101 statt 1, damit ein Skript einen Absturz von einem regulären `return 1;`
            // unterscheiden kann (Rusts Konvention).
            return 101;
        }
        catch (LyricRuntimeException ex)
        {
            de.Report(ex.Code, Severity.Error, default, ex.Message);
            de.RenderText(Console.Error);
            return 1;
        }
    }

    /// <summary>Compiliert nach <c>.lyrbc</c>. Ohne <c>-o</c> neben die Quelle.</summary>
    private static int Build(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("build: missing file argument");
            return 2;
        }
        var fpath = args[1];
        var output = OutputPath(args) ?? Path.ChangeExtension(fpath, ".lyrbc");

        var pipeline = Compile(fpath);
        if (pipeline.Ir is null) return 1;

        try
        {
            File.WriteAllBytes(output, BytecodeWriter.Write(pipeline.Ir));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"build: cannot write {output}: {ex.Message}");
            return 1;
        }

        Console.Out.WriteLine($"{output}: {new FileInfo(output).Length} bytes");
        return 0;
    }

    /// <summary>Liest ein <c>.lyrbc</c> und druckt es lesbar. Validiert dabei vollständig —
    /// ADR-013: ein Modul wird beim Laden geprüft, nicht beim Ausführen.</summary>
    private static int Disasm(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("disasm: missing file argument");
            return 2;
        }
        var fpath = args[1];

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(fpath);
        }
        catch
        {
            Console.Error.WriteLine($"disasm: failed to read file: {fpath}");
            return 1;
        }

        var de = new DiagnosticEngine(new SourceManager());
        var module = BytecodeReader.Read(bytes, de);
        de.RenderText(Console.Error);
        if (module is null) return 1;

        Console.Out.Write(Disassembler.Dump(module));
        return 0;
    }

    private static string? OutputPath(string[] args)
    {
        for (var i = 2; i < args.Length - 1; i++)
            if (args[i] is "-o" or "--output") return args[i + 1];
        return null;
    }

    /// <summary>Quelle → IR, mit allen Diagnosen gerendert. <c>Ir == null</c> heißt: abgebrochen.</summary>
    private static (IrModule? Ir, DiagnosticEngine De) Compile(string fpath)
    {
        var sm = new SourceManager();
        var de = new DiagnosticEngine(sm);
        FileId id;
        try
        {
            id = sm.AddFromDisk(fpath);
        }
        catch
        {
            de.Report("LYR-CLI0001", Severity.Error, default, $"failed to read file: {fpath}");
            de.RenderText(Console.Error);
            return (null, de);
        }

        var comp = new Compilation(sm, de);
        comp.AddModule(new Parser(sm, id, de).ParseModule());
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        if (de.HasErrors)
        {
            de.RenderText(Console.Error);
            return (null, de);
        }

        var ir = ModuleLowerer.Lower(comp, types, de);
        de.RenderText(Console.Error);
        return (ir, de);
    }

    /// <summary>Debug-Ausgabe der Mid-IR (M5/P4), analog zu 'parse'. Lowert nur, wenn die Sema
    /// fehlerfrei war — auf fehlerhaftem AST wäre jedes Lowering-Ergebnis Raten.</summary>
    private static int Lower(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("lower: missing file argument");
            return 2;
        }
        var fpath = args[1];
        var sm = new SourceManager();
        var de = new DiagnosticEngine(sm);
        FileId id;
        try
        {
            id = sm.AddFromDisk(fpath);
        }
        catch
        {
            de.Report("LYR-CLI0001", Severity.Error, default, $"failed to read file: {fpath}");
            de.RenderText(Console.Error);
            return 1;
        }
        var module = new Parser(sm, id, de).ParseModule();
        var comp = new Compilation(sm, de);
        comp.AddModule(module);
        var binding = comp.Resolve();
        var types = Semantics.Analyze(comp, binding, de);

        if (de.HasErrors)
        {
            de.RenderText(Console.Error);
            return 1;
        }

        // Scope-Grenzen des Lowerings kommen als LYR-IR0001 in dieselbe DiagnosticEngine und
        // werden mit Datei/Zeile/Spalte gerendert wie jeder andere Fehler auch.
        var ir = ModuleLowerer.Lower(comp, types, de);
        de.RenderText(Console.Error);
        if (ir is null) return 1;

        Console.Out.Write(IrPrinter.Dump(ir));
        return 0;
    }

    private static int CheckCmd(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("check: missing file argument");
            return 2;
        }
        var fpath = args[1];
        var sm = new SourceManager();
        var de = new DiagnosticEngine(sm);
        FileId id;
        try
        {
            id = sm.AddFromDisk(fpath);
        }
        catch
        {
            de.Report("LYR-CLI0001", Severity.Error, default, $"failed to read file: {fpath}");
            de.RenderText(Console.Error);
            return 1;
        }
        var module = new Parser(sm, id, de).ParseModule();
        var comp = new Compilation(sm, de);
        comp.AddModule(module);
        var binding = comp.Resolve();
        Semantics.Analyze(comp, binding, de);

        de.RenderText(Console.Error);
        if (de.HasErrors) return 1;
        Console.Out.WriteLine($"{fpath}: ok");
        return 0;
    }

    private static int Parse(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("parse: missing file argument");
            return 2;
        }
        var fpath = args[1];
        var sm = new SourceManager();
        var de = new DiagnosticEngine(sm);
        FileId id;
        try
        {
            id = sm.AddFromDisk(fpath);
        }
        catch
        {
            de.Report("LYR-CLI0001", Severity.Error, default, $"failed to read file: {fpath}");
            de.RenderText(Console.Error);
            return 1;
        }
        var module = new Parser(sm, id, de).ParseModule();
        Console.Out.Write(AstDumper.Dump(module, sm));
        de.RenderText(Console.Error);
        return de.HasErrors ? 1 : 0;
    }

    private static int Tokenize(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("tokenize: missing file argument");
            return 2;
        }
        var fpath = args[1];
        var sm = new SourceManager();
        var de = new DiagnosticEngine(sm);
        FileId id;
        try
        {
            id = sm.AddFromDisk(fpath);
        }
        catch
        {
            id = FileId.None;
            de.Report("LYR-CLI0001", Severity.Error, default, $"failed to read file: {fpath}");
        }
        var lex = new Lexer(sm, id, de);
        var tl = new List<Token>();
        Token t;
        do
        {
            t = lex.Next();
            tl.Add(t);
        } while (t.TokenKind != TokenKind.Eof);
        Console.Out.Write(TokenDumper.Dump(tl, sm));
        de.RenderText(Console.Error);
        return de.HasErrors ? 1 : 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"lyric {Version}");
        return 0;
    }

    private static int HelpAndOk()
    {
        PrintHelp();
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"unknown command: {cmd}");
        Console.Error.WriteLine("try 'lyric --help'");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("lyric — compiler and VM for the Lyric language");
        Console.WriteLine();
        Console.WriteLine("Usage: lyric <command> [args]");
        Console.WriteLine();
        Console.WriteLine("Commands (M0 stub — more coming):");
        Console.WriteLine("  --version, -v    Show version");
        Console.WriteLine("  --help, -h       Show this help");
        Console.WriteLine("  tokenize <file>  Print token stream (debug)");
        Console.WriteLine("  parse <file>     Print AST dump (debug)");
        Console.WriteLine("  check <file>     Resolve + type-check (no build)");
        Console.WriteLine("  lower <file>     Print mid-IR dump (debug)");
        Console.WriteLine("  build <file> [-o <out>]  Compile to .lyrbc");
        Console.WriteLine("  disasm <file>    Disassemble a .lyrbc");
        Console.WriteLine("  run <file>       Compile and execute (.lyr or .lyrbc)");
    }
}
