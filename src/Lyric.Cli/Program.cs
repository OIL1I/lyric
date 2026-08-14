using Lyric.Core;

namespace Lyric.Cli;

/// <summary>
/// <c>lyric</c> — the driver of the tool suite.
///
/// <para>It compiles nothing and executes nothing. It selects tools, translates convenience
/// commands into tool commands and passes results through: <c>lyric run app.lyr</c> is
/// <c>lyrc build</c> followed by <c>lyrvm run</c>. It references neither <c>lyrfe</c> nor
/// <c>lyrrt</c>.</para>
///
/// <para>The debug dumps (<c>tokenize</c>, <c>parse</c>, <c>lower</c>) live in <c>lyrc</c> and are
/// not reachable from here.</para>
/// </summary>
public static class Program
{
    public static int Main(string[] rawArgs)
    {
        var (selection, args, error) = ToolSelection.Parse(rawArgs);
        if (error is not null)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                error, ExitCodes.Usage);

        if (args.Length == 0) { PrintHelp(); return ExitCodes.Success; }

        return args[0] switch
        {
            "--version" or "-v" => Version(selection),
            "--help" or "-h" => Help(),
            "run" => Run(args, selection),
            "build" or "check" => Forward(Tool.Compiler, selection, args),
            "disasm" => Forward(Tool.Runtime, selection, args),

            "repl" => Forward(Tool.Repl, selection, args),
            _ => CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownCommand,
                $"unknown command: {args[0]} — try 'lyric --help'", ExitCodes.Usage),
        };
    }

    /// <summary>
    /// Compile and execute. A path that already ends in <c>.lyrbc</c> skips the compile step.
    ///
    /// <para>The intermediate module is a temporary file and is deleted when the run ends.</para>
    /// </summary>
    private static int Run(string[] args, ToolSelection selection)
    {
        var separator = Array.IndexOf(args, "--");
        var positional = separator < 0 ? args : args[..separator];
        var programArguments = separator < 0 ? [] : args[(separator + 1)..];

        if (positional.Length < 2)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                "run: missing file argument", ExitCodes.Usage);

        var path = positional[1];
        var passThrough = positional[2..];

        // Checked before the compile step so a misconfigured runtime is reported without one.
        if (Missing(selection, Tool.Runtime) is { } runtimeError) return runtimeError;

        if (path.EndsWith(".lyrbc", StringComparison.OrdinalIgnoreCase))
            return Execute(selection, path, passThrough, programArguments);

        if (Missing(selection, Tool.Compiler) is { } compilerError) return compilerError;

        // The name carries the source file name so a backtrace from the runtime stays readable.
        var module = Path.Combine(Path.GetTempPath(),
            $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.lyrbc");

        try
        {
            // '--quiet' suppresses the compiler's summary of an artifact that is about to be
            // deleted. Passing it twice is harmless.
            var built = Tool.Run(selection.PathOf(Tool.Compiler),
                ["build", path, "-o", module, "--quiet", .. passThrough], Console.Error);
            if (built != ExitCodes.Success) return built;

            return Execute(selection, module, [], programArguments);
        }
        finally
        {
            // Runs on every path, including a panic in the executed program.
            try { File.Delete(module); } catch (IOException) { /* nothing further to do */ }
        }
    }

    private static int Execute(ToolSelection selection, string module, string[] options,
        string[] programArguments)
    {
        string[] tail = programArguments.Length == 0 ? [] : ["--", .. programArguments];
        return Tool.Run(selection.PathOf(Tool.Runtime),
            ["run", module, .. options, .. tail], Console.Error);
    }

    /// <summary>Passes a command through to a tool unchanged, including every option the driver
    /// does not know.</summary>
    private static int Forward(Tool tool, ToolSelection selection, string[] args)
    {
        if (Missing(selection, tool) is { } error) return error;
        return Tool.Run(selection.PathOf(tool), args, Console.Error);
    }

    private static int? Missing(ToolSelection selection, Tool tool)
    {
        var path = selection.PathOf(tool);
        return File.Exists(path)
            ? null
            : CliDiagnostics.Fail(Console.Error, CliDiagnostics.VmNotFound,
                $"{tool.Name} not found: {path} (set {tool.EnvironmentVariable} or pass {tool.Flag})",
                ExitCodes.Failure);
    }

    private static int Version(ToolSelection selection)
    {
        Console.Out.WriteLine($"lyric {ToolchainVersion.Value}");

        // Column width from the list, so a new tool cannot outgrow it.
        var width = Tool.All.Max(tool => tool.Name.Length);

        foreach (var tool in Tool.All)
            Console.Out.WriteLine($"  {tool.Name.PadRight(width)} {selection.DisplayOf(tool)}");
        return ExitCodes.Success;
    }

    private static int Help() { PrintHelp(); return ExitCodes.Success; }

    private static void PrintHelp()
    {
        Console.Out.WriteLine("""
            lyric - the Lyric tool suite

            Usage: lyric <command> <file> [options] [-- <program args>]

            Commands:
              run <file>               Compile and execute (.lyr or .lyrbc)
              build <file> [-o <out>]  Compile .lyr to .lyrbc
              check <file>             Compile without writing a file
              disasm <file.lyrbc>      Print a readable disassembly
              repl                     Start a REPL session

            Options:
              --compiler <path>        Compiler to use; defaults to $LYRIC_COMPILER,
                                       then the bundled lyrc
              --vm <path>              Runtime to use; defaults to $LYRIC_VM,
                                       then the bundled lyrvm
              --version, -v            Show versions and the selected tools
              --help, -h               Show this help

            Every other option is passed straight to the tool that runs the command.
            For compiler internals (tokenize, parse, lower) call 'lyrc' directly;
            to inspect a module (verify, info) call 'lyrvm'.
            """);
    }
}
