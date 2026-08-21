using Lyric.Bytecode;
using Lyric.Core;
using Lyric.Vm;

namespace Lyric.Cli.Runtime;

/// <summary>
/// <c>lyrvm</c> — the bundled runtime and the reference implementation of the runner contract in
/// <c>docs/Bytecode.md</c> §8.
///
/// <para>It accepts <c>.lyrbc</c> only; <c>run</c> on a <c>.lyr</c> source is an error, not a
/// forward to the compiler. The project references nothing compiler-side: no lexer, parser, sema,
/// IR or bytecode writer.</para>
/// </summary>
public static class Program
{
    public static int Main(string[] rawArgs)
    {
        ConsoleStreams.UseUtf8WhenRedirected();

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
        // Runner contract (Bytecode.md §8.5): everything after the first '--' belongs to the
        // program. A parameterless 'main' ignores it.
        var programArguments = ProgramArguments(args);

        terminal.BeginPhase(Phase.Read, Path.GetFileName(args[1]));
        var module = VmHost.Load(bytes, Console.Error);
        terminal.EndPhase();
        if (module is null) return ExitCodes.Failure;

        // '--grant' limits what the module may reach. Without it the standalone mode grants
        // everything.
        var granted = Capability.All;
        var grantIndex = Array.IndexOf(args, "--grant");
        if (grantIndex >= 0)
        {
            if (grantIndex + 1 >= args.Length)
                return CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownCommand,
                    "'--grant' needs a list, e.g. '--grant file,os' or '--grant none'",
                    ExitCodes.Usage);

            if (CapabilityTable.Parse(args[grantIndex + 1]) is not { } parsed)
                return CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownCommand,
                    $"unknown capability in '{args[grantIndex + 1]}' — known are "
                    + "file, net, os, host, all, none", ExitCodes.Usage);
            granted = parsed;
        }

        terminal.Finish();
        return VmHost.Execute(module, programArguments, Console.Out, Console.Error, granted);
    }

    /// <summary>Loads the module completely, then prints it readably. <c>--function</c> narrows
    /// the output to one function; the module header stays.</summary>
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

    /// <summary>Format validation and import binding, without executing an instruction.</summary>
    private static int Verify(byte[] bytes, string[] args, TerminalOutput terminal)
    {
        var code = VmHost.Verify(bytes, Console.Out, Console.Error);
        if (code == ExitCodes.Success) terminal.Info($"{args[1]}: ok");
        return code;
    }

    /// <summary>Header fields and table counts. This is payload, so <c>--quiet</c> does not
    /// suppress it.</summary>
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
    /// The shared preamble: the argument is present, the extension matches, the file is readable.
    /// Every command takes exactly one <c>.lyrbc</c>.
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

    /// <summary>Everything after the first <c>--</c>: the Lyric program's arguments.</summary>
    private static string[] ProgramArguments(string[] args)
    {
        var separator = Array.IndexOf(args, "--");
        return separator < 0 ? [] : args[(separator + 1)..];
    }

    /// <summary>The value of an option that appears before the <c>--</c>.</summary>
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
