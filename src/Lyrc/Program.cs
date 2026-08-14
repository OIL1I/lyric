using Lyric.AST;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Ir;
using Lyric.Lexing;

namespace Lyric.Cli.Compiler;

/// <summary>
/// <c>lyrc</c> — the compiler.
///
/// <para>One job per invocation; it never executes anything. The debug dumps
/// (<c>tokenize</c>, <c>parse</c>, <c>lower</c>) live here rather than in the driver.</para>
///
/// <para>Every command runs through <see cref="SourceCompiler"/>. This program holds no pipeline
/// logic, only argument handling and output.</para>
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

    /// <summary>Compiles to <c>.lyrbc</c>. Without <c>-o</c> the output lands next to the
    /// source.</summary>
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

    /// <summary>Everything a build does except writing the file.</summary>
    private static int Check(string path, string[] args, TerminalOutput terminal)
    {
        var result = SourceCompiler.Check(path, Options(args, terminal));
        terminal.Render(result.Diagnostics);
        if (!result.Ok) return ExitCodes.Failure;

        terminal.Info($"{path}: ok");
        return ExitCodes.Success;
    }

    /// <summary>Debug output of the mid-level IR. Lowers only when sema reported no errors.
    /// </summary>
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

    /// <summary>What the compiler needs besides the file. <c>--stdlib</c> beats
    /// <c>LYRIC_STDLIB</c>.</summary>
    private static CompilerOptions Options(string[] args, TerminalOutput terminal) => new()
    {
        StdlibRoot = Flag(args, "--stdlib"),
        Progress = terminal,
    };

    /// <summary>Every command here takes exactly one required file; the check lives in one
    /// place.</summary>
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
        Console.Out.WriteLine("  check <file>             Compile without writing a file");
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
