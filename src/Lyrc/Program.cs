using Lyric.AST;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Ir;
using Lyric.Lexing;

namespace Lyric.Cli.Compiler;

/// <summary>
/// <c>lyrc</c> — der Compiler (ADR-017).
///
/// <para>Technische Oberflaeche: ein Job pro Aufruf, keine Bequemlichkeit, keine Ausfuehrung.
/// Die Debug-Dumps (<c>tokenize</c>, <c>parse</c>, <c>lower</c>) wohnen hier und <b>nicht</b> im
/// Treiber — sie sind Compiler-Interna, und genau daran haengt der Unterschied zwischen
/// „technisch" und „komfortabel".</para>
///
/// <para>Alle Kommandos laufen ueber <see cref="SourceCompiler"/>. Dieses Programm enthaelt keine
/// Pipeline-Logik, nur Argument-Auswertung und Ausgabe.</para>
/// </summary>
public static class Program
{
    public static int Main(string[] rawArgs)
    {
        var (options, args, optionError) = ToolOptions.Parse(rawArgs);
        if (optionError is not null)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownCommand,
                optionError, ExitCodes.Usage);

        using var terminal = new TerminalOutput(Console.Out, Console.Error, options);

        if (args.Length == 0) { PrintHelp(); return ExitCodes.Success; }

        return args[0] switch
        {
            "--version" or "-v" => Version(terminal),
            "--help" or "-h" => Help(),
            "build" => WithFile(args, "build", terminal, Build),
            "check" => WithFile(args, "check", terminal, Check),
            "lower" => WithFile(args, "lower", terminal, Lower),
            "parse" => WithFile(args, "parse", terminal, Parse),
            "tokenize" => WithFile(args, "tokenize", terminal, Tokenize),
            _ => CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownCommand,
                $"unknown command: {args[0]} — try 'lyrc --help'", ExitCodes.Usage),
        };
    }

    /// <summary>Compiliert nach <c>.lyrbc</c>. Ohne <c>-o</c> neben die Quelle.</summary>
    private static int Build(string path, string[] args, TerminalOutput terminal)
    {
        var output = Flag(args, "-o") ?? Flag(args, "--output")
            ?? Path.ChangeExtension(path, ".lyrbc");

        var result = SourceCompiler.Compile(path, Options(args, terminal));
        terminal.Render(result.Diagnostics);
        if (!result.Ok || result.Bytes is null) return ExitCodes.Failure;

        try
        {
            File.WriteAllBytes(output, result.Bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.OutputUnwritable,
                $"cannot write {output}: {ex.Message}", ExitCodes.Failure);
        }

        terminal.Info($"{output}: {new FileInfo(output).Length} bytes");
        return ExitCodes.Success;
    }

    /// <summary>Resolve + Sema, sonst nichts. Kein Lowering, keine Datei erzeugt.</summary>
    private static int Check(string path, string[] args, TerminalOutput terminal)
    {
        var result = SourceCompiler.Check(path, Options(args, terminal));
        terminal.Render(result.Diagnostics);
        if (!result.Ok) return ExitCodes.Failure;

        terminal.Info($"{path}: ok");
        return ExitCodes.Success;
    }

    /// <summary>Debug-Ausgabe der Mid-IR. Lowert nur, wenn die Sema fehlerfrei war — auf
    /// fehlerhaftem AST waere jedes Lowering-Ergebnis Raten.</summary>
    private static int Lower(string path, string[] args, TerminalOutput terminal)
    {
        var result = SourceCompiler.Lower(path, Options(args, terminal));
        terminal.Render(result.Diagnostics);
        if (!result.Ok || result.Ir is null) return ExitCodes.Failure;

        terminal.Payload(IrPrinter.Dump(result.Ir));
        return ExitCodes.Success;
    }

    private static int Parse(string path, string[] args, TerminalOutput terminal)
    {
        var (sources, diagnostics, id) = SourceCompiler.Read(path);
        if (!id.IsValid) { terminal.Render(diagnostics); return ExitCodes.Failure; }

        var module = new Parsing.Parser(sources, id, diagnostics).ParseModule();
        terminal.Payload(AstDumper.Dump(module, sources));
        terminal.Render(diagnostics);
        return diagnostics.HasErrors ? ExitCodes.Failure : ExitCodes.Success;
    }

    private static int Tokenize(string path, string[] args, TerminalOutput terminal)
    {
        var (sources, diagnostics, id) = SourceCompiler.Read(path);
        if (!id.IsValid) { terminal.Render(diagnostics); return ExitCodes.Failure; }

        var lexer = new Lexer(sources, id, diagnostics);
        var tokens = new List<Token>();
        Token token;
        do
        {
            token = lexer.Next();
            tokens.Add(token);
        } while (token.TokenKind != TokenKind.Eof);

        terminal.Payload(TokenDumper.Dump(tokens, sources));
        terminal.Render(diagnostics);
        return diagnostics.HasErrors ? ExitCodes.Failure : ExitCodes.Success;
    }

    /// <summary>Was der Compiler ausser der Datei braucht. <c>--stdlib</c> schlaegt
    /// <c>LYRIC_STDLIB</c> — dieselbe Staffelung wie <c>--vm</c>/<c>LYRIC_VM</c> im Treiber.</summary>
    private static CompilerOptions Options(string[] args, TerminalOutput terminal) => new()
    {
        StdlibRoot = Flag(args, "--stdlib"),
        Progress = terminal,
    };

    /// <summary>Jedes Kommando hier nimmt genau eine Pflicht-Datei. Die Pruefung einmal statt
    /// fuenfmal — die alte CLI hatte sie kopiert, mit fuenf leicht verschiedenen Meldungen.</summary>
    private static int WithFile(string[] args, string command, TerminalOutput terminal,
        Func<string, string[], TerminalOutput, int> run)
    {
        if (args.Length < 2)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                $"{command}: missing file argument", ExitCodes.Usage);
        return run(args[1], args, terminal);
    }

    private static string? Flag(string[] args, string name)
    {
        for (var i = 2; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static int Version(TerminalOutput terminal)
    {
        terminal.Payload($"lyrc {ToolchainVersion.Value}\n");
        return ExitCodes.Success;
    }

    private static int Help() { PrintHelp(); return ExitCodes.Success; }

    private static void PrintHelp()
    {
        Console.Out.WriteLine("lyrc — the Lyric compiler");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Usage: lyrc <command> <file> [options]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Commands:");
        Console.Out.WriteLine("  build <file> [-o <out>]  Compile .lyr to .lyrbc");
        Console.Out.WriteLine("  check <file>             Resolve and type-check only");
        Console.Out.WriteLine("  lower <file>             Print the mid-IR dump (debug)");
        Console.Out.WriteLine("  parse <file>             Print the AST dump (debug)");
        Console.Out.WriteLine("  tokenize <file>          Print the token stream (debug)");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  --stdlib <dir>           Where the stdlib lives (beats $LYRIC_STDLIB)");
        Console.Out.WriteLine("  --json                   Diagnostics as JSON on stderr");
        Console.Out.WriteLine("  --quiet, -q              Suppress success messages");
        Console.Out.WriteLine("  --verbose                Print a per-phase timing breakdown");
        Console.Out.WriteLine("  --progress <mode>        auto (default), never or always");
        Console.Out.WriteLine("  --version, -v            Show the toolchain version");
        Console.Out.WriteLine("  --help, -h               Show this help");
        Console.Out.WriteLine();
        Console.Out.WriteLine("lyrc does not execute anything. Use 'lyrvm run' or 'lyric run'.");
    }
}
