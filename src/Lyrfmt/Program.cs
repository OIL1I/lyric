using Lyric.Core;
using Lyric.Formatting;

namespace Lyric.Cli.Fmt;

/// <summary>
/// <c>lyrfmt</c> — the formatter. One shape, no style options: a flag per taste would make the
/// formatter a second place where a project's look is negotiated, and gofmt's lesson is that
/// nobody misses the negotiation.
///
/// <para>Three ways in: files and directories (formatted IN PLACE, a directory recursively),
/// <c>--check</c> (writes nothing, lists what would change, exit 1 when anything would — the CI
/// form), and <c>--stdin</c> (stdout carries the result — the editor form). A file that does
/// not parse is reported and left EXACTLY as it was; the run continues and fails at the
/// end.</para>
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

        var check = args.Contains("--check");
        var stdin = args.Contains("--stdin");
        var paths = args.Where(a => a is not ("--check" or "--stdin")).ToArray();

        return args[0] switch
        {
            "--version" or "-v" => Version(terminal),
            "--help" or "-h" => Help(),
            _ when stdin && (paths.Length > 0 || check) => CliDiagnostics.Fail(Console.Error,
                CliDiagnostics.UnknownCommand,
                "--stdin formats exactly one stream to stdout; paths and --check do not combine "
                + "with it", ExitCodes.Usage),
            _ when stdin => FromStdin(),
            _ when paths.Length == 0 => CliDiagnostics.Fail(Console.Error,
                CliDiagnostics.MissingArgument, "missing path argument — a file, or a directory "
                + "to format recursively", ExitCodes.Usage),
            _ => Files(paths, check, terminal),
        };
    }

    private static int FromStdin()
    {
        var source = Console.In.ReadToEnd();
        var sources = new SourceManager();
        var file = sources.AddVirtual("<stdin>", source);
        var diagnostics = new DiagnosticEngine(sources);

        var formatted = Formatter.Format(sources, file, diagnostics);
        if (formatted is null)
        {
            diagnostics.RenderText(Console.Error);
            return ExitCodes.Failure;
        }

        Console.Out.Write(formatted);
        return ExitCodes.Success;
    }

    private static int Files(string[] paths, bool check, TerminalOutput terminal)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                files.AddRange(Directory.GetFiles(path, "*.lyr", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal));
            else if (File.Exists(path))
                files.Add(path);
            else
                return CliDiagnostics.Fail(Console.Error, CliDiagnostics.FileUnreadable,
                    $"no such file or directory: {path}", ExitCodes.Failure);
        }

        var failed = false;
        var wouldChange = false;
        foreach (var file in files)
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CliDiagnostics.Fail(Console.Error, CliDiagnostics.FileUnreadable,
                    $"failed to read file: {file}", ExitCodes.Failure);
                failed = true;
                continue;
            }

            // A fresh manager per file: spans are coupled to the manager they were made in.
            var sources = new SourceManager();
            var id = sources.AddVirtual(file, source);
            var diagnostics = new DiagnosticEngine(sources);

            var formatted = Formatter.Format(sources, id, diagnostics);
            if (formatted is null)
            {
                // Reported, untouched, and the run carries on: one broken file must not hide
                // whether the others are clean.
                diagnostics.RenderText(Console.Error);
                failed = true;
                continue;
            }

            if (formatted == source) continue;

            wouldChange = true;
            if (check)
            {
                terminal.Payload(file + "\n");
                continue;
            }

            try
            {
                File.WriteAllText(file, formatted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CliDiagnostics.Fail(Console.Error, CliDiagnostics.OutputUnwritable,
                    $"cannot write {file}: {ex.Message}", ExitCodes.Failure);
                failed = true;
                continue;
            }

            terminal.Info($"{file}: formatted");
        }

        if (failed) return ExitCodes.Failure;
        return check && wouldChange ? ExitCodes.Failure : ExitCodes.Success;
    }

    private static int Version(TerminalOutput terminal)
    {
        terminal.Payload($"lyrfmt {ToolchainVersion.Value}\n");
        return ExitCodes.Success;
    }

    private static int Help() { PrintHelp(); return ExitCodes.Success; }

    private static void PrintHelp()
    {
        Console.Out.WriteLine("lyrfmt — the Lyric formatter");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Usage: lyrfmt <path>... [options]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Paths are files or directories; a directory means every .lyr under it.");
        Console.Out.WriteLine("Files are formatted in place. A file that does not parse is reported");
        Console.Out.WriteLine("and left untouched.");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  --check               Write nothing; list files that would change and");
        Console.Out.WriteLine("                        exit 1 when any would");
        Console.Out.WriteLine("  --stdin               Format standard input to standard output");
        Console.Out.WriteLine("  --json                Diagnostics as JSON on stderr");
        Console.Out.WriteLine("  --quiet, -q           Suppress success messages");
        Console.Out.WriteLine("  --version, -v         Show the toolchain version");
        Console.Out.WriteLine("  --help, -h            Show this help");
        Console.Out.WriteLine();
        Console.Out.WriteLine("There are no style options. The shape is the tool's contract.");
    }
}
