using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Vm;

namespace Lyric.Cli.Runtime;

/// <summary>
/// <c>lyrvm</c> — die mitgelieferte Runtime (ADR-017) und zugleich die Referenz-Implementierung
/// des Runner-Vertrags aus <c>docs/Bytecode.md</c>.
///
/// <para>Kennt ausschliesslich <c>.lyrbc</c>. <c>run</c> auf einer <c>.lyr</c>-Quelle ist ein
/// Fehler und keine stille Weiterleitung an den Compiler: eine Runtime, die Quelltext frisst, ist
/// keine Runtime mehr, und die Trennung waere nach zwei Wochen wieder weich.</para>
///
/// <para>Dieses Projekt referenziert nichts Compiler-seitiges — kein Lexer, kein Parser, keine
/// Sema, keine IR, keinen Bytecode-Writer. Das ist der Punkt der ganzen Uebung und der Grund,
/// warum <c>tests/Lyric.Tests.Cli</c> es maschinell festhaelt.</para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) { PrintHelp(); return ExitCodes.Success; }

        return args[0] switch
        {
            "--version" or "-v" => Version(),
            "--help" or "-h" => Help(),
            "run" => WithModule(args, "run", Run),
            "disasm" => WithModule(args, "disasm", Disasm),
            "verify" => WithModule(args, "verify", Verify),
            _ => CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownCommand,
                $"unknown command: {args[0]} — try 'lyrvm --help'", ExitCodes.Usage),
        };
    }

    private static int Run(byte[] bytes, string[] args)
    {
        // Der Vertrag sieht `-- <args>` vor, die Sprache loest es noch nicht ein: ModuleLowerer
        // nimmt nur ein parameterloses `main` als Einstieg (Sprache.md §11 kennt auch
        // `main(args: string[])`). Ablehnen statt still verwerfen — sonst taeuscht die Runtime
        // vor, Argumente zugestellt zu haben.
        if (ProgramArguments(args) is { Length: > 0 })
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.ProgramArgumentsUnsupported,
                "program arguments are not supported yet: 'fn main(args: string[])' is specified "
                + "(Sprache.md §11) but not lowered — only a parameterless 'main' is an entry point",
                ExitCodes.Usage);

        return VmHost.Execute(bytes, Console.Out, Console.Error);
    }

    /// <summary>Laedt vollstaendig — ADR-013 prueft beim Laden, nicht beim Ausfuehren — und druckt
    /// das Modul lesbar.</summary>
    private static int Disasm(byte[] bytes, string[] args)
    {
        var module = VmHost.Load(bytes, Console.Error);
        if (module is null) return ExitCodes.Failure;

        Console.Out.Write(Disassembler.Dump(module));
        return ExitCodes.Success;
    }

    /// <summary>Format-Validierung und Import-Bindung, ohne eine Instruktion auszufuehren. Fuer
    /// eine Fremd-Runtime ist das der Konformanz-Test gegen ein gegebenes Modul.</summary>
    private static int Verify(byte[] bytes, string[] args)
    {
        var code = VmHost.Verify(bytes, Console.Out, Console.Error);
        if (code == ExitCodes.Success) Console.Out.WriteLine($"{args[1]}: ok");
        return code;
    }

    /// <summary>
    /// Der gemeinsame Vorspann: Argument da, Endung stimmt, Datei lesbar. Alle drei Kommandos
    /// nehmen genau ein <c>.lyrbc</c>.
    /// </summary>
    private static int WithModule(string[] args, string command, Func<byte[], string[], int> run)
    {
        if (args.Length < 2)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                $"{command}: missing file argument", ExitCodes.Usage);

        var path = args[1];
        if (!Path.GetExtension(path).Equals(".lyrbc", StringComparison.OrdinalIgnoreCase))
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.WrongFileKind,
                $"{command}: expected a .lyrbc module, got '{path}' — lyrvm does not compile. "
                + "Use 'lyrc build' first, or 'lyric run' to do both.",
                ExitCodes.Usage);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.FileUnreadable,
                $"failed to read file: {path}", ExitCodes.Failure);
        }

        return run(bytes, args);
    }

    /// <summary>Alles nach dem ersten <c>--</c>. Der Vertrag trennt so die Argumente des Runners
    /// von denen des Lyric-Programms.</summary>
    private static string[] ProgramArguments(string[] args)
    {
        var separator = Array.IndexOf(args, "--");
        return separator < 0 ? [] : args[(separator + 1)..];
    }

    private static int Version()
    {
        Console.Out.WriteLine($"lyrvm {ToolchainVersion.Value} (.lyrbc "
            + $"{Format.VersionMajor}.{Format.VersionMinor})");
        return ExitCodes.Success;
    }

    private static int Help() { PrintHelp(); return ExitCodes.Success; }

    private static void PrintHelp()
    {
        Console.Out.WriteLine("lyrvm — the bundled Lyric runtime");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Usage: lyrvm <command> <file.lyrbc> [-- <program args>]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Commands:");
        Console.Out.WriteLine("  run <file.lyrbc>     Load, validate and execute");
        Console.Out.WriteLine("  disasm <file.lyrbc>  Print a readable disassembly");
        Console.Out.WriteLine("  verify <file.lyrbc>  Validate and bind imports, execute nothing");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  --version, -v        Show toolchain and .lyrbc format version");
        Console.Out.WriteLine("  --help, -h           Show this help");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Exit codes: main's return value & 0xFF · 101 panic · 1 load error"
            + " · 2 usage error");
        Console.Out.WriteLine("lyrvm does not compile. Use 'lyrc build' or 'lyric run'.");
    }
}
