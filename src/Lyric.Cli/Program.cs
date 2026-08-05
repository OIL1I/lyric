using Lyric.Bytecode;
using Lyric.Compiler;
using Lyric.Core;
using Lyric.Vm;

namespace Lyric.Cli;

/// <summary>
/// <c>lyric</c> — der Treiber (ADR-017).
///
/// <para>Die bequeme Oberflaeche: <c>run</c> auf einer Quelle compiliert und fuehrt in einem
/// Schritt aus, <c>build</c> und <c>check</c> reichen durch, <c>--vm</c> waehlt die Runtime.</para>
///
/// <para><b>Dieser Treiber hat keine eigene Compile- oder Ausfuehrungslogik.</b> Er ruft
/// <see cref="SourceCompiler"/> und <see cref="VmHost"/> — dieselben Einstiege wie <c>lyrc</c> und
/// <c>lyrvm</c>. Der Grund steht im Projekt-Gedaechtnis: als drei Kommandos je eine eigene Kopie
/// des Vorspanns hatten, verdrahtete nur eine den ModuleLoader, und <c>check</c> prueste stumm gar
/// nicht. Drei Binaries sind drei neue Gelegenheiten fuer genau diesen Fehler.</para>
///
/// <para>Die Debug-Dumps (<c>tokenize</c>, <c>parse</c>, <c>lower</c>) gibt es hier bewusst
/// <b>nicht</b> — sie sind Compiler-Interna und wohnen in <c>lyrc</c>.</para>
/// </summary>
public static class Program
{
    public static int Main(string[] rawArgs)
    {
        var (vm, args, flagError) = VmSelection.Parse(rawArgs);
        if (flagError is not null)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                flagError, ExitCodes.Usage);

        if (args.Length == 0) { PrintHelp(); return ExitCodes.Success; }

        return args[0] switch
        {
            "--version" or "-v" => Version(vm),
            "--help" or "-h" => Help(),
            "run" => Run(args, vm),
            "build" => Build(args),
            "check" => Check(args),
            "disasm" => Disasm(args),
            _ => CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownCommand,
                $"unknown command: {args[0]} — try 'lyric --help'", ExitCodes.Usage),
        };
    }

    /// <summary>
    /// Compiliert bei Bedarf und fuehrt aus. Nimmt <c>.lyr</c> (compiliert im Speicher) oder
    /// <c>.lyrbc</c> (laedt direkt).
    ///
    /// <para>Zwei Pfade, und das ist eine bewusste Entscheidung: mit der mitgelieferten Runtime
    /// laeuft alles in-process, eine Fremd-Runtime bekommt einen Subprozess. Sie kann keine
    /// In-Memory-Bytes entgegennehmen — sie braucht eine Datei, also wird bei
    /// <c>.lyr</c>-Eingabe eine temporaere erzeugt und danach wieder entfernt.</para>
    /// </summary>
    private static int Run(string[] args, VmSelection vm)
    {
        var separator = Array.IndexOf(args, "--");
        var positional = separator < 0 ? args : args[..separator];
        var programArguments = separator < 0 ? [] : args[(separator + 1)..];

        if (positional.Length < 2)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                "run: missing file argument", ExitCodes.Usage);

        var path = positional[1];

        // Vor dem Compilieren pruefen, nicht danach: sonst kaeme die Meldung ueber eine falsch
        // konfigurierte Runtime erst nach einem vollstaendigen Compile-Lauf.
        if (!vm.Exists())
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.VmNotFound,
                $"runtime not found: {vm.ExecutablePath} "
                + $"(from --vm or {VmSelection.EnvironmentVariable})", ExitCodes.Failure);

        return IsModule(path)
            ? RunModule(path, programArguments, vm)
            : RunSource(path, programArguments, vm);
    }

    private static int RunModule(string path, string[] programArguments, VmSelection vm)
    {
        if (!vm.IsBundled) return vm.RunForeign(path, programArguments, Console.Error);

        if (RejectProgramArguments(programArguments) is { } rejected) return rejected;

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

        return VmHost.Execute(bytes, Console.Out, Console.Error);
    }

    private static int RunSource(string path, string[] programArguments, VmSelection vm)
    {
        var result = SourceCompiler.Compile(path);
        if (!result.Render(Console.Error) || result.Bytes is null) return ExitCodes.Failure;

        if (vm.IsBundled)
        {
            if (RejectProgramArguments(programArguments) is { } rejected) return rejected;
            return VmHost.Execute(result.Bytes, Console.Out, Console.Error);
        }

        // Eine Fremd-Runtime braucht eine Datei. Der Name traegt die PID, damit parallele Laeufe
        // sich nicht gegenseitig ueberschreiben.
        var temporary = Path.Combine(Path.GetTempPath(),
            $"lyric-{Environment.ProcessId}-{Path.GetFileNameWithoutExtension(path)}.lyrbc");
        try
        {
            File.WriteAllBytes(temporary, result.Bytes);
            return vm.RunForeign(temporary, programArguments, Console.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.OutputUnwritable,
                $"cannot write temporary module {temporary}: {ex.Message}", ExitCodes.Failure);
        }
        finally
        {
            // Laeuft auch, wenn die Fremd-Runtime abstuerzt.
            try { File.Delete(temporary); } catch (IOException) { /* nicht der Rede wert */ }
        }
    }

    private static int Build(string[] args)
    {
        if (args.Length < 2)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                "build: missing file argument", ExitCodes.Usage);

        var path = args[1];
        var output = OutputPath(args) ?? Path.ChangeExtension(path, ".lyrbc");

        var result = SourceCompiler.Compile(path);
        if (!result.Render(Console.Error) || result.Bytes is null) return ExitCodes.Failure;

        try
        {
            File.WriteAllBytes(output, result.Bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.OutputUnwritable,
                $"cannot write {output}: {ex.Message}", ExitCodes.Failure);
        }

        Console.Out.WriteLine($"{output}: {new FileInfo(output).Length} bytes");
        return ExitCodes.Success;
    }

    private static int Check(string[] args)
    {
        if (args.Length < 2)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                "check: missing file argument", ExitCodes.Usage);

        if (!SourceCompiler.Check(args[1]).Render(Console.Error)) return ExitCodes.Failure;
        Console.Out.WriteLine($"{args[1]}: ok");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Disassembliert — <b>immer</b> mit dem mitgelieferten Disassembler, auch wenn eine
    /// Fremd-Runtime konfiguriert ist. Das Format ist spezifiziert, seine Textdarstellung ist es
    /// nicht; „was steht in dieser Datei" ist deshalb keine Frage an die gewaehlte Runtime.
    /// </summary>
    private static int Disasm(string[] args)
    {
        if (args.Length < 2)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                "disasm: missing file argument", ExitCodes.Usage);

        var path = args[1];
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

        var module = VmHost.Load(bytes, Console.Error);
        if (module is null) return ExitCodes.Failure;

        Console.Out.Write(Disassembler.Dump(module));
        return ExitCodes.Success;
    }

    /// <summary>Siehe <c>LYR-CLI0007</c>: der Runner-Vertrag sieht Programm-Argumente vor, die
    /// Sprache loest sie noch nicht ein. Nur der In-Process-Pfad muss das hier pruefen — eine
    /// Fremd-Runtime beantwortet die Frage selbst.</summary>
    private static int? RejectProgramArguments(string[] programArguments) =>
        programArguments.Length == 0
            ? null
            : CliDiagnostics.Fail(Console.Error, CliDiagnostics.ProgramArgumentsUnsupported,
                "program arguments are not supported yet: 'fn main(args: string[])' is specified "
                + "(Sprache.md §11) but not lowered — only a parameterless 'main' is an entry point",
                ExitCodes.Usage);

    private static bool IsModule(string path) =>
        Path.GetExtension(path).Equals(".lyrbc", StringComparison.OrdinalIgnoreCase);

    private static string? OutputPath(string[] args)
    {
        for (var i = 2; i < args.Length - 1; i++)
            if (args[i] is "-o" or "--output") return args[i + 1];
        return null;
    }

    private static int Version(VmSelection vm)
    {
        Console.Out.WriteLine($"lyric {ToolchainVersion.Value} (.lyrbc "
            + $"{Format.VersionMajor}.{Format.VersionMinor}, runtime: {vm.Display})");
        return ExitCodes.Success;
    }

    private static int Help() { PrintHelp(); return ExitCodes.Success; }

    private static void PrintHelp()
    {
        Console.Out.WriteLine("lyric — the Lyric toolchain driver");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Usage: lyric <command> <file> [options] [-- <program args>]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Commands:");
        Console.Out.WriteLine("  run <file>               Compile and execute (.lyr or .lyrbc)");
        Console.Out.WriteLine("  build <file> [-o <out>]  Compile .lyr to .lyrbc");
        Console.Out.WriteLine("  check <file>             Resolve and type-check only");
        Console.Out.WriteLine("  disasm <file.lyrbc>      Print a readable disassembly");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  --vm <path>              Runtime to execute with; defaults to");
        Console.Out.WriteLine($"                           ${VmSelection.EnvironmentVariable}, then the bundled one");
        Console.Out.WriteLine("  --version, -v            Show versions and the active runtime");
        Console.Out.WriteLine("  --help, -h               Show this help");
        Console.Out.WriteLine();
        Console.Out.WriteLine("For compiler internals (tokenize, parse, lower) use 'lyrc'.");
        Console.Out.WriteLine("To run a module without compiling, use 'lyrvm'.");
    }
}
