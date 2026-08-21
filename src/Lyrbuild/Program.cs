using Lyric.Compiler;
using Lyric.Core;
using Lyric.Embedding;

namespace Lyric.Cli.Build;

/// <summary>
/// What a build script declares: one program to compile.
///
/// <para>A host object, so the script holds it as a value and configures it after the call that
/// produced it. Everything on it is read after <c>build</c> has returned.</para>
/// </summary>
public sealed class Artifact
{
    public required string Entry { get; init; }
    public required string Output { get; init; }

    /// <summary>Whether the module carries a source map. The default of <c>lyric build</c>.</summary>
    public bool SourceMap { get; set; } = true;
}

/// <summary>
/// <c>lyrbuild</c> — runs a <c>build.lyr</c> and compiles what it declares.
///
/// <para>The second binary that holds both libraries, for the same reason as <c>lyrrepl</c>: a
/// build script is a Lyric program that has to RUN, and what it declares has to be COMPILED
/// afterwards. Two subprocesses cannot do it — the artifacts live in the objects the script was
/// handed.</para>
///
/// <para>NOTHING IS COMPILED WHILE THE SCRIPT RUNS. It collects, and the compiles happen once
/// <c>build</c> has returned, so an option set on the line after <c>addExecutable</c> still applies
/// and any file the script generates is finished before it is read.</para>
///
/// <para>A build script runs with every capability. It writes files and starts processes, which is
/// the point of it being a script rather than a manifest — and it means <c>lyric build</c> in a
/// repository you did not write runs code you did not write, exactly as <c>make</c> and
/// <c>cmake</c> do.</para>
/// </summary>
public static class Program
{
    /// <summary>The file searched for in the directory the build was pointed at.</summary>
    public const string FileName = "build.lyr";

    public static int Main(string[] args)
    {
        ConsoleStreams.UseUtf8WhenRedirected();

        if (args.Contains("--help") || args.Contains("-h")) { PrintHelp(); return ExitCodes.Success; }
        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.Out.WriteLine($"lyrbuild {ToolchainVersion.Value}");
            return ExitCodes.Success;
        }

        var directory = Path.GetFullPath(
            args.FirstOrDefault(a => !a.StartsWith('-')) ?? Directory.GetCurrentDirectory());

        var script = Path.Combine(directory, FileName);
        if (!File.Exists(script))
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.NoBuildScript,
                $"no {FileName} in {directory}", ExitCodes.Usage);

        try
        {
            return Run(script, directory, Flag(args, "--stdlib"));
        }
        catch (ProjectFileException broken)
        {
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.BadProjectFile,
                $"{broken.Path}: {broken.Message}", ExitCodes.Failure);
        }
    }

    private static int Run(string script, string directory, string? stdlibRoot)
    {
        // The roots belong to the project and not to the build: lyric.json answers them once, for
        // the script itself and for everything it declares.
        var project = ProjectFile.Discover(directory);
        foreach (var warning in project?.Warnings ?? [])
            Console.Error.WriteLine(
                $"warning: {Path.Combine(project!.Directory, ProjectFile.FileName)}: {warning}");

        var artifacts = new List<Artifact>();
        var vm = new LangVm(new HostOptions
        {
            // A build script writes files and starts processes. Withholding that would leave a
            // manifest with parentheses.
            Capabilities = Capability.FileAccess | Capability.NetworkAccess | Capability.OsAccess,
            StdlibRoot = stdlibRoot,
            Output = Console.Out,
            Error = Console.Error,
        });

        // Opaque: the script holds an Artifact and configures it, and never looks inside.
        vm.RegisterType<Artifact>("Artifact");

        // Registered under the names the DECLARATIONS in stdlib/std/build.lyr produce. The lowering
        // mangles a method as '<module>.<Type>.<method>', which is what a native is looked up by.
        vm.RegisterNative("std.build.addExecutable", (string entry, string output) =>
        {
            var artifact = new Artifact
            {
                Entry = Path.GetFullPath(Path.Combine(directory, entry)),
                Output = Path.GetFullPath(Path.Combine(directory, output)),
            };
            artifacts.Add(artifact);
            return artifact;
        });

        // A BLOCK body, not an expression one: 'artifact.SourceMap = on' is an assignment
        // expression and would make this a Func<Artifact, bool, bool>, which does not match the
        // 'void' the declaration promises.
        vm.RegisterNative("std.build.Artifact.sourceMap",
            (Artifact artifact, bool on) => { artifact.SourceMap = on; });

        ScriptInstance instance;
        try
        {
            instance = vm.Instantiate(vm.CompileFile(script));
        }
        catch (EmbeddingException)
        {
            // Compiled again to report it. EmbeddingException carries the diagnostics as data but
            // not the SourceManager their spans point into, and a message without a line is worse
            // than the second compile is slow — this path ends the build either way.
            var result = SourceCompiler.Check(script, new CompilerOptions { StdlibRoot = stdlibRoot });
            var writer = new StringWriter();
            result.Diagnostics.RenderText(writer);
            Console.Error.Write(writer.ToString());
            return ExitCodes.Failure;
        }
        catch (ScriptException refused)
        {
            // The module loaded and could not be bound or run: a native the host did not register,
            // a capability it does not grant.
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.BuildScriptFailed,
                $"{FileName}: {refused.Message}", ExitCodes.Failure);
        }

        // A relative path has to mean the same thing everywhere in the script. 'addExecutable'
        // resolves against the project; without this, a 'writeText("src/x.lyr", …)' beside it would
        // resolve against whatever directory the build was started from.
        var callerDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(directory);

        try
        {
            instance.CallVoid("build");
        }
        catch (ScriptPanicException panic)
        {
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.BuildScriptFailed,
                $"{FileName}: {panic.Message}", ExitCodes.Failure);
        }
        catch (ScriptException refused)
        {
            // No 'build' function, or one with a shape nobody can call. ScriptPanicException is
            // caught above, so what reaches here is a script that never ran rather than one that
            // failed while running.
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.BuildScriptFailed,
                $"{FileName}: {refused.Message}", ExitCodes.Failure);
        }
        finally
        {
            Directory.SetCurrentDirectory(callerDirectory);
        }

        if (artifacts.Count == 0)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.BuildScriptFailed,
                $"{FileName}: 'build' declared nothing to compile", ExitCodes.Failure);

        return Compile(artifacts, project, stdlibRoot);
    }

    /// <summary>Compiles what the script collected. Every artifact is a whole program of its own;
    /// there is no link step and nothing is shared between them but the source on disk.</summary>
    private static int Compile(List<Artifact> artifacts, ProjectFile? project, string? stdlibRoot)
    {
        var failed = false;

        foreach (var artifact in artifacts)
        {
            if (!File.Exists(artifact.Entry))
            {
                CliDiagnostics.Fail(Console.Error, CliDiagnostics.FileUnreadable,
                    $"{artifact.Entry}: no such file", ExitCodes.Failure);
                failed = true;
                continue;
            }

            var result = SourceCompiler.Compile(artifact.Entry, new CompilerOptions
            {
                StdlibRoot = stdlibRoot,
                SourceRoot = project?.SourceRoot,
                NativeRoots = project?.NativeRoots,
                SourceMap = artifact.SourceMap,
            });

            var writer = new StringWriter();
            result.Diagnostics.RenderText(writer);
            Console.Error.Write(writer.ToString());

            if (!result.Ok || result.Bytes is null) { failed = true; continue; }

            try
            {
                var parent = Path.GetDirectoryName(artifact.Output);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.WriteAllBytes(artifact.Output, result.Bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CliDiagnostics.Fail(Console.Error, CliDiagnostics.OutputUnwritable,
                    $"{artifact.Output}: {ex.Message}", ExitCodes.Failure);
                failed = true;
                continue;
            }

            Console.Out.WriteLine($"{artifact.Output}: {result.Bytes.Length} bytes");
        }

        return failed ? ExitCodes.Failure : ExitCodes.Success;
    }

    private static string? Flag(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static void PrintHelp()
    {
        Console.Out.WriteLine("Usage: lyrbuild [directory] [options]");
        Console.Out.WriteLine();
        Console.Out.WriteLine($"Runs the {FileName} in the directory and compiles what it declares.");
        Console.Out.WriteLine("Without a directory, the working directory.");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Options:");
        Console.Out.WriteLine("  --stdlib <dir>   Where the stdlib lives (beats $LYRIC_STDLIB)");
        Console.Out.WriteLine("  --version, -v    Show the toolchain version");
        Console.Out.WriteLine("  --help, -h       Show this help");
        Console.Out.WriteLine();
        Console.Out.WriteLine("A build script runs with every capability: it may write files and");
        Console.Out.WriteLine("start processes, like make or cmake.");
    }
}
