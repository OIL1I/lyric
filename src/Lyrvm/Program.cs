using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Vm;

namespace Lyric.Cli.Runtime;

/// <summary>
/// <c>lyrvm</c> — die mitgelieferte Runtime (ADR-017) und zugleich die Referenz-Implementierung
/// des Runner-Vertrags aus <c>docs/Bytecode.md</c> §9.
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
            "run" => WithModule(args, "run", terminal, Run),
            "disasm" => WithModule(args, "disasm", terminal, Disasm),
            "verify" => WithModule(args, "verify", terminal, Verify),
            "info" => WithModule(args, "info", terminal, Info),
            _ => CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownCommand,
                $"unknown command: {args[0]} — try 'lyrvm --help'", ExitCodes.Usage),
        };
    }

    private static int Run(byte[] bytes, string[] args, TerminalOutput terminal)
    {
        // Punkt 4 des Runner-Vertrags (Bytecode.md §9): alles nach dem ersten '--' gehoert dem
        // Programm. Ein parameterloses 'main' ignoriert es — das ist kein Fehler, sondern
        // dieselbe Freiheit, die jede Shell hat.
        var programArguments = ProgramArguments(args);

        terminal.BeginPhase(Phase.Read, Path.GetFileName(args[1]));
        var module = VmHost.Load(bytes, Console.Error);
        terminal.EndPhase();
        if (module is null) return ExitCodes.Failure;

        // Die Anzeige muss weg, BEVOR die erste Instruktion laeuft — sonst landet die erste
        // Ausgabe des Programms neben einer halben Fortschrittszeile.
        terminal.Finish();
        return VmHost.Execute(module, programArguments, Console.Out, Console.Error);
    }

    /// <summary>Laedt vollstaendig — ADR-013 prueft beim Laden, nicht beim Ausfuehren — und druckt
    /// das Modul lesbar. Mit <c>--function</c> nur eine Funktion, Modulkopf bleibt.</summary>
    private static int Disasm(byte[] bytes, string[] args, TerminalOutput terminal)
    {
        var module = VmHost.Load(bytes, Console.Error);
        if (module is null) return ExitCodes.Failure;

        var only = Flag(args, "--function");
        var dump = Disassembler.Dump(module, only);
        if (dump is null)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownFunction,
                $"no function named '{only}' in {args[1]}", ExitCodes.Failure);

        terminal.Payload(dump);
        return ExitCodes.Success;
    }

    /// <summary>Format-Validierung und Import-Bindung, ohne eine Instruktion auszufuehren. Fuer
    /// eine Fremd-Runtime ist das der Konformanz-Test gegen ein gegebenes Modul.</summary>
    private static int Verify(byte[] bytes, string[] args, TerminalOutput terminal)
    {
        var code = VmHost.Verify(bytes, Console.Out, Console.Error);
        if (code == ExitCodes.Success) terminal.Info($"{args[1]}: ok");
        return code;
    }

    /// <summary>Kopfdaten und Tabellen-Zaehler. Nutzlast, nicht Plauderei — <c>--quiet</c> darf
    /// das nicht schlucken.</summary>
    private static int Info(byte[] bytes, string[] args, TerminalOutput terminal)
    {
        var module = VmHost.Load(bytes, Console.Error);
        if (module is null) return ExitCodes.Failure;

        terminal.Payload(terminal.Options.Json
            ? ModuleInfo.Json(module, args[1]) + "\n"
            : ModuleInfo.Text(module, args[1]));
        return ExitCodes.Success;
    }

    /// <summary>
    /// Der gemeinsame Vorspann: Argument da, Endung stimmt, Datei lesbar. Alle Kommandos nehmen
    /// genau ein <c>.lyrbc</c>.
    /// </summary>
    private static int WithModule(string[] args, string command, TerminalOutput terminal,
        Func<byte[], string[], TerminalOutput, int> run)
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

        return run(bytes, args, terminal);
    }

    /// <summary>Alles nach dem ersten <c>--</c>. Der Vertrag trennt so die Argumente des Runners
    /// von denen des Lyric-Programms.</summary>
    private static string[] ProgramArguments(string[] args)
    {
        var separator = Array.IndexOf(args, "--");
        return separator < 0 ? [] : args[(separator + 1)..];
    }

    /// <summary>Wert einer Option, die vor dem <c>--</c> steht.</summary>
    private static string? Flag(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--") return null;
            if (args[i] == name) return args[i + 1];
        }
        return null;
    }

    private static int Version(TerminalOutput terminal)
    {
        terminal.Payload($"lyrvm {ToolchainVersion.Value} (.lyrbc "
            + $"{Format.VersionMajor}.{Format.VersionMinor})\n");
        return ExitCodes.Success;
    }

    private static int Help() { PrintHelp(); return ExitCodes.Success; }

    private static void PrintHelp()
    {
        Console.Out.WriteLine("lyrvm — the bundled Lyric runtime");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Usage: lyrvm <command> <file.lyrbc> [options] [-- <program args>]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Commands:");
        Console.Out.WriteLine("  run <file.lyrbc>      Load, validate and execute");
        Console.Out.WriteLine("  disasm <file.lyrbc>   Print a readable disassembly");
        Console.Out.WriteLine("  verify <file.lyrbc>   Validate and bind imports, execute nothing");
        Console.Out.WriteLine("  info <file.lyrbc>     Print header, table counts and functions");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  --function <name>     disasm: only this function");
        Console.Out.WriteLine("  --json                Diagnostics (and 'info') as JSON");
        Console.Out.WriteLine("  --quiet, -q           Suppress success messages");
        Console.Out.WriteLine("  --verbose             Print a per-phase timing breakdown");
        Console.Out.WriteLine("  --progress <mode>     auto (default), never or always");
        Console.Out.WriteLine("  --version, -v         Show toolchain and .lyrbc format version");
        Console.Out.WriteLine("  --help, -h            Show this help");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Exit codes: main's return value & 0xFF · 101 panic · 1 load error"
            + " · 2 usage error");
        Console.Out.WriteLine("lyrvm does not compile. Use 'lyrc build' or 'lyric run'.");
    }
}
