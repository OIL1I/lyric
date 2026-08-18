using System.Diagnostics;
using Lyric.Core;

namespace Lyric.Cli;

/// <summary>A tool of the suite: its name, its selection flag and its environment variable.
/// </summary>
public sealed record Tool(string Name, string Flag, string EnvironmentVariable)
{
    /// <summary>The compiler: source to <c>.lyrbc</c>.</summary>
    public static readonly Tool Compiler = new("lyrc", "--compiler", "LYRIC_COMPILER");

    /// <summary>The runtime: executes a <c>.lyrbc</c>.</summary>
    public static readonly Tool Runtime = new("lyrvm", "--vm", "LYRIC_VM");

    /// <summary>The interactive prompt. It holds both libraries, because it compiles and executes
    /// and keeps state between entries.</summary>
    public static readonly Tool Repl = new("lyrrepl", "--repl", "LYRIC_REPL");

    /// <summary>The build runner: executes a <c>build.lyr</c> and compiles what it declares. Holds
    /// both libraries for the same reason the REPL does.</summary>
    public static readonly Tool Builder = new("lyrbuild", "--builder", "LYRIC_BUILD");

    /// <summary>The packer: a <c>.lyrbc</c> into one executable. It neither compiles nor
    /// executes; <c>lyric pack app.lyr</c> composes it with the compiler.</summary>
    public static readonly Tool Packer = new("lyrpack", "--packer", "LYRIC_PACK");

    public static readonly IReadOnlyList<Tool> All = [Compiler, Runtime, Repl, Builder, Packer];

    /// <summary>Where the tool lives: <c>--flag &lt;path&gt;</c> beats the environment variable,
    /// which beats the executable next to this one.</summary>
    public string Resolve(string? fromFlag)
    {
        if (!string.IsNullOrWhiteSpace(fromFlag)) return fromFlag;

        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        return Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? $"{Name}.exe" : Name);
    }

    /// <summary>The name for messages: "bundled", or the path that was selected.</summary>
    public string Display(string? fromFlag) =>
        string.IsNullOrWhiteSpace(fromFlag)
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable))
            ? "bundled"
            : Resolve(fromFlag);

    /// <summary>
    /// Starts the tool and waits for it.
    ///
    /// <para>stdin, stdout and stderr are inherited, not redirected, so the child keeps TTY
    /// detection, colour and interactivity, and the stream separation of the runner contract
    /// (docs/Bytecode.md §8.3) holds.</para>
    /// </summary>
    public static int Run(string executable, IEnumerable<string> arguments, TextWriter error)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return CliDiagnostics.Fail(error, CliDiagnostics.VmLaunchFailed,
                    $"could not start '{executable}'", ExitCodes.Failure);

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            return CliDiagnostics.Fail(error, CliDiagnostics.VmLaunchFailed,
                $"could not start '{executable}': {ex.Message}", ExitCodes.Failure);
        }
    }
}
