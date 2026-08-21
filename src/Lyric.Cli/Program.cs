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
        ConsoleStreams.UseUtf8WhenRedirected();

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
            "new" => NewProject.Run(args),
            "build" => Build(args, selection),
            "pack" => Pack(args, selection),
            "fmt" => StripVerbAndForward(Tool.Fmt, selection, args),
            "test" => StripVerbAndForward(Tool.Test, selection, args),
            "check" => Forward(Tool.Compiler, selection, args),
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

    /// <summary>
    /// Compile and pack, or pack alone: a <c>.lyrbc</c> goes to the packer as it stands, a source
    /// is compiled into a temporary module first — the composition of <c>run</c>, with an
    /// executable instead of an execution.
    ///
    /// <para>The pack options (<c>-o</c>, <c>--stub</c>) are taken out here and everything else
    /// travels to the compiler. The output is ALWAYS passed explicitly on the source path: the
    /// packer's default names the executable after its input, and its input is a temporary file
    /// whose name is nobody's deliverable.</para>
    /// </summary>
    private static int Pack(string[] args, ToolSelection selection)
    {
        if (Missing(selection, Tool.Packer) is { } packerError) return packerError;

        string? file = null, output = null, stub = null;
        var compilerArguments = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--":
                    // 'run' forwards what follows to the program; here there is no program run.
                    return CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownCommand,
                        "pack: '--' has no place here — a packed program receives its arguments "
                        + "when it runs", ExitCodes.Usage);

                case "-o" or "--output" or "--stub":
                    if (i + 1 >= args.Length)
                        return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                            $"{args[i]}: missing path argument", ExitCodes.Usage);
                    if (args[i] == "--stub") stub = args[++i];
                    else output = args[++i];
                    break;

                default:
                    if (file is null && !args[i].StartsWith('-')) file = args[i];
                    else compilerArguments.Add(args[i]);
                    break;
            }
        }

        if (file is null)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                "pack: missing file argument", ExitCodes.Usage);

        string[] stubOption = stub is null ? [] : ["--stub", stub];

        if (file.EndsWith(".lyrbc", StringComparison.OrdinalIgnoreCase))
        {
            string[] outputOption = output is null ? [] : ["-o", output];
            return Tool.Run(selection.PathOf(Tool.Packer),
                [file, .. outputOption, .. stubOption, .. compilerArguments], Console.Error);
        }

        if (Missing(selection, Tool.Compiler) is { } compilerError) return compilerError;

        var executable = output ?? DefaultExecutable(file);
        var module = Path.Combine(Path.GetTempPath(),
            $"{Path.GetFileNameWithoutExtension(file)}-{Guid.NewGuid():N}.lyrbc");

        try
        {
            var built = Tool.Run(selection.PathOf(Tool.Compiler),
                ["build", file, "-o", module, "--quiet", .. compilerArguments], Console.Error);
            if (built != ExitCodes.Success) return built;

            return Tool.Run(selection.PathOf(Tool.Packer),
                [module, "-o", executable, .. stubOption], Console.Error);
        }
        finally
        {
            try { File.Delete(module); } catch (IOException) { /* nothing further to do */ }
        }
    }

    /// <summary>The source's own name as an executable, beside the source — the same place the
    /// compiler drops a module nobody named.</summary>
    private static string DefaultExecutable(string source)
    {
        var directory = Path.GetDirectoryName(source) ?? "";
        var name = Path.GetFileNameWithoutExtension(source);
        return Path.Combine(directory, OperatingSystem.IsWindows() ? $"{name}.exe" : name);
    }

    /// <summary>
    /// <c>build</c> with a source file is the compiler; without one, or with a directory, it is the
    /// build script that lies there.
    ///
    /// <para>Decided on the argument rather than on a flag, because the two are different
    /// questions: "compile this file" and "build this project". A path that does not exist stays
    /// with the compiler, whose diagnostic names the file it could not read.</para>
    /// </summary>
    private static int Build(string[] args, ToolSelection selection)
    {
        var positional = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));

        if (positional is not null && !Directory.Exists(positional))
            return Forward(Tool.Compiler, selection, args);

        // Without the verb: it is the driver's word for which tool to start, not an argument the
        // tool itself takes.
        if (Missing(selection, Tool.Builder) is { } error) return error;
        return Tool.Run(selection.PathOf(Tool.Builder), args[1..], Console.Error);
    }

    /// <summary>Passes a command through to a tool unchanged, including every option the driver
    /// does not know.</summary>
    private static int Forward(Tool tool, ToolSelection selection, string[] args)
    {
        if (Missing(selection, tool) is { } error) return error;
        return Tool.Run(selection.PathOf(tool), args, Console.Error);
    }

    /// <summary>Like <see cref="Forward"/>, minus the verb: it is the driver's word for which
    /// tool to start, not an argument the tool itself takes — the build runner's pattern.
    /// </summary>
    private static int StripVerbAndForward(Tool tool, ToolSelection selection, string[] args)
    {
        if (Missing(selection, tool) is { } error) return error;
        return Tool.Run(selection.PathOf(tool), args[1..], Console.Error);
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
              new <name> [--lib]       Write a new project, an app or a library
              run <file>               Compile and execute (.lyr or .lyrbc)
              build <file> [-o <out>]  Compile .lyr to .lyrbc
              build [<dir>]            Run the build.lyr there and compile what it declares
              pack <file> [-o <out>]   Compile and pack into one standalone executable
              fmt <path>... [--check]  Format .lyr files in place (--check only lists)
              test [<dir>]             Run the @Test functions of the project's test root
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
