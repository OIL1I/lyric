using System.Diagnostics;

namespace Lyric.Tests.Cli;

/// <summary>What a toolchain call leaves behind.</summary>
public sealed record ToolResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>Line endings normalized: the goldens of this project compare text rather than byte
    /// offsets, and CRLF is pure platform cosmetics here.</summary>
    public string Out => StdOut.Replace("\r\n", "\n");

    public string Err => StdErr.Replace("\r\n", "\n");
}

/// <summary>
/// Starts <c>lyrc</c>, <c>lyrvm</c> and <c>lyric</c> as real processes.
///
/// <para>Deliberately across the process boundary rather than through <c>Program.Main</c>: the questions
/// of this test project are exit codes, stream separation and what lies next to the binary. None of
/// them can be answered honestly in process.</para>
/// </summary>
public static class Toolchain
{
    /// <summary>The repository root, found through <c>Lyric.slnx</c>. The way up from the test assembly is
    /// more stable than a counted <c>../../../..</c>, which becomes silently wrong at every change to the
    /// output structure.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>Debug or release: the tests run in the same configuration as the binaries they
    /// check.</summary>
    private static string Configuration { get; } =
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    public static string LyrcPath => BinaryPath("Lyrc", "lyrc");
    public static string LyrvmPath => BinaryPath("Lyrvm", "lyrvm");
    public static string LyricPath => BinaryPath("Lyric.Cli", "lyric");

    /// <summary>The directory a binary and its dependencies lie in: the basis of the architecture test.
    /// </summary>
    public static string OutputDirectory(string project) =>
        Path.Combine(RepositoryRoot, "src", project, "bin", Configuration, "net10.0");

    public static string Example(string name) => Path.Combine(RepositoryRoot, "examples", name);

    public static ToolResult Lyrc(params string[] args) => Run(LyrcPath, args);
    public static ToolResult Lyrvm(params string[] args) => Run(LyrvmPath, args);
    public static ToolResult Lyric(params string[] args) => Run(LyricPath, args);

    public static string LyrreplPath => BinaryPath("Lyrrepl", "lyrrepl");

    public static string LyrlsPath => BinaryPath("Lyrls", "lyrls");

    public static string LyrbuildPath => BinaryPath("Lyrbuild", "lyrbuild");

    public static ToolResult Lyrbuild(params string[] args) => Run(LyrbuildPath, args);

    /// <summary>
    /// Runs a tool and writes something to its stdin, for the REPL, which cannot be checked otherwise.
    ///
    /// <para>The input stream is CLOSED after the last line. An EOF is the end for the REPL (Ctrl+D), so
    /// it exits even without a ':quit'; without the close the test would wait until the timeout.</para>
    /// </summary>
    public static ToolResult RunWithInput(string executable, string[] args, string input)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryRoot,
        };
        foreach (var argument in args) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        process.StandardInput.Write(input);
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ToolResult(process.ExitCode, output, error);
    }

    /// <summary>
    /// Like <see cref="Run(string, string[])"/>, but with environment variables for THIS PROCESS ONLY.
    ///
    /// <para>Not <c>Environment.SetEnvironmentVariable</c> in the test process: xUnit runs test classes in
    /// parallel, and a set variable would then affect every compiler started at the same time. This
    /// project got stuck on exactly that at the first full run — green in isolation, red together. Shared
    /// mutable state between tests is the cause; a collection attribute would only be the plaster.</para>
    /// </summary>
    public static ToolResult RunWithEnvironment(string executable,
        IReadOnlyDictionary<string, string?> environment, params string[] args) =>
        Run(executable, environment, args);

    public static ToolResult Run(string executable, params string[] args) =>
        Run(executable, null, args);

    private static ToolResult Run(string executable,
        IReadOnlyDictionary<string, string?>? environment, string[] args)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryRoot,
        };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment) info.Environment[key] = value;

        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException($"could not start {executable}");

        // Read first, then wait: in the other order a child blocks as soon as it writes more than fits
        // into the pipe.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new ToolResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>A scratch path that cleans itself up.</summary>
    public static TemporaryFile Temp(string extension) => new(extension);

    /// <summary>A scratch DIRECTORY that cleans itself up. What a multi-file program needs: its
    /// module root is a directory, not a file.</summary>
    public static TemporaryDirectory TempDirectory() => new();

    private static string BinaryPath(string project, string binary)
    {
        var name = OperatingSystem.IsWindows() ? $"{binary}.exe" : binary;
        var path = Path.Combine(OutputDirectory(project), name);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"'{binary}' was not built at {path} — the test project references it, so this "
                + "means the build layout changed.", path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lyric.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Lyric.slnx not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>A temporary file that disappears when the scope is left.</summary>
public sealed class TemporaryFile(string extension) : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"lyric-test-{Guid.NewGuid():N}{extension}");

    public void Dispose()
    {
        try { File.Delete(Path); } catch (IOException) { /* not worth mentioning */ }
    }
}

/// <summary>A temporary directory that disappears with everything in it.</summary>
public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"lyric-test-{Guid.NewGuid():N}");

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    /// <summary>Writes a module into the directory and returns its full path, creating the
    /// sub-directories a dotted module path needs.</summary>
    public string Write(string relativePath, string text)
    {
        var file = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { /* as above */ }
    }
}
