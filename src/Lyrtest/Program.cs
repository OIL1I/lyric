using Lyric.Compiler;
using Lyric.Core;
using Lyric.Embedding;
using Lyric.Vm;

namespace Lyric.Cli.Test;

/// <summary>
/// <c>lyrtest</c> — runs the <c>@Test</c> functions of a project's test root.
///
/// <para>The Go shape, not the Rust shape: tests live in a directory of their own
/// (<c>testRoot</c> in <c>lyric.json</c>, <c>tests/</c> by default) that only this runner ever
/// compiles — production builds never see them, so <c>@Test</c> stays a TOOL-read attribute and
/// no build rule hangs off it.</para>
///
/// <para>Discovery and execution go through the embedding API — the attribute rows and call
/// handles a host uses — which makes this binary the first consumer of that machinery that is
/// not a test of it. Each test runs in a fresh instance: state cannot leak between tests,
/// because there is no shared instance to leak through.</para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        ConsoleStreams.UseUtf8WhenRedirected();

        string? directoryArgument = null;
        string? stdlib = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--version" or "-v":
                    Console.Out.WriteLine($"lyrtest {ToolchainVersion.Value}");
                    return ExitCodes.Success;
                case "--help" or "-h":
                    PrintHelp();
                    return ExitCodes.Success;
                case "--stdlib" when i + 1 < args.Length:
                    stdlib = args[++i];
                    break;
                default:
                    if (args[i].StartsWith('-') || directoryArgument is not null)
                        return CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownCommand,
                            $"unknown argument: {args[i]} — try 'lyrtest --help'", ExitCodes.Usage);
                    directoryArgument = args[i];
                    break;
            }
        }

        var directory = Path.GetFullPath(directoryArgument ?? ".");
        if (!Directory.Exists(directory))
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.FileUnreadable,
                $"no such directory: {directory}", ExitCodes.Failure);

        ProjectFile? project;
        try
        {
            project = ProjectFile.Discover(directory);
        }
        catch (ProjectFileException broken)
        {
            // A named testRoot that is no directory lands here too: the project file validates
            // its own paths, and a named root is a promise — unlike the default below, which is
            // a convention.
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.BadProjectFile,
                $"{broken.Path}: {broken.Message}", ExitCodes.Failure);
        }

        foreach (var warning in project?.Warnings ?? [])
            CliDiagnostics.Warn(Console.Error, CliDiagnostics.ProjectFileSuspect,
                $"{Path.Combine(project!.Directory, ProjectFile.FileName)}: {warning}");

        // A project that simply has no tests/ has no tests — the Go answer, not an error.
        var testRoot = project?.TestRoot
            ?? Path.Combine(project?.Directory ?? directory, "tests");
        if (!Directory.Exists(testRoot))
        {
            Console.Out.WriteLine("no tests");
            return ExitCodes.Success;
        }

        var files = Directory.GetFiles(testRoot, "*.lyr", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0)
        {
            Console.Out.WriteLine("no tests");
            return ExitCodes.Success;
        }

        // Every capability: a test is the user's own code run on their own machine, the same
        // standing as 'lyric run'. Output goes through, which is how a failing test gets to
        // print what it saw.
        var vm = new LangVm(new HostOptions
        {
            Capabilities = Capability.All,
            StdlibRoot = stdlib,
            SourceRoot = project?.SourceRoot,
            NativeRoots = project?.NativeRoots,
            Output = Console.Out,
            Error = Console.Error,
        });

        var total = 0;
        var failed = 0;
        foreach (var file in files)
        {
            ScriptModule module;
            try
            {
                module = vm.CompileFile(file);
            }
            catch (EmbeddingException)
            {
                // Compiled a second time only on the failure path, because the exception carries
                // the diagnostics as data and rendering wants the sources they point into.
                var result = SourceCompiler.Check(file, new CompilerOptions
                {
                    StdlibRoot = stdlib,
                    SourceRoot = project?.SourceRoot,
                    NativeRoots = project?.NativeRoots,
                });
                result.Diagnostics.RenderText(Console.Error);
                failed++;
                continue;
            }

            foreach (var test in module.Attributes.OnFunctions("Test"))
            {
                total++;
                try
                {
                    vm.Instantiate(module).CallVoid(test);
                    Console.Out.WriteLine($"PASS {test.TargetName}");
                }
                catch (ScriptPanicException panic)
                {
                    failed++;
                    Console.Out.WriteLine($"FAIL {test.TargetName}: {panic.Message}");
                    if (panic.InnerException is LyricPanic { CallStack: { } frames })
                        foreach (var frame in frames)
                            Console.Out.WriteLine($"    in {frame}");
                }
                catch (ScriptException error)
                {
                    failed++;
                    Console.Out.WriteLine($"FAIL {test.TargetName}: [{error.Code}] {error.Message}");
                }
            }
        }

        Console.Out.WriteLine(failed == 0
            ? $"{total} test(s), all passed"
            : $"{total} test(s), {failed} FAILED");
        return failed == 0 ? ExitCodes.Success : ExitCodes.Failure;
    }

    private static void PrintHelp()
    {
        Console.Out.WriteLine("lyrtest — runs the @Test functions of a project's test root");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Usage: lyrtest [directory] [options]");
        Console.Out.WriteLine();
        Console.Out.WriteLine("The directory (default: the current one) locates the project's");
        Console.Out.WriteLine("lyric.json; its testRoot (default: tests/) is compiled, and every");
        Console.Out.WriteLine("function marked @Test runs in a fresh instance. A test fails by");
        Console.Out.WriteLine("panicking — std.test has the assertions.");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  --stdlib <dir>           Where the stdlib lives (beats $LYRIC_STDLIB)");
        Console.Out.WriteLine("  --version, -v            Show the toolchain version");
        Console.Out.WriteLine("  --help, -h               Show this help");
    }
}
