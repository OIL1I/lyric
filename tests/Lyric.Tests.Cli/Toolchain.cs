using System.Diagnostics;

namespace Lyric.Tests.Cli;

/// <summary>Was ein Toolchain-Aufruf hinterlaesst.</summary>
public sealed record ToolResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>Zeilenenden normalisiert — die Goldens dieses Projekts vergleichen Text, nicht
    /// Byte-Offsets, und CRLF ist hier reine Plattform-Kosmetik.</summary>
    public string Out => StdOut.Replace("\r\n", "\n");

    public string Err => StdErr.Replace("\r\n", "\n");
}

/// <summary>
/// Startet <c>lyrc</c>, <c>lyrvm</c> und <c>lyric</c> als echte Prozesse.
///
/// <para>Bewusst ueber die Prozess-Grenze und nicht ueber <c>Program.Main</c>: die Fragen dieses
/// Testprojekts sind Exit-Codes, Stream-Trennung und was neben dem Binary liegt. Keine davon
/// laesst sich in-process ehrlich beantworten.</para>
/// </summary>
public static class Toolchain
{
    /// <summary>Das Repo-Wurzelverzeichnis, gefunden ueber <c>Lyric.slnx</c>. Der Weg vom
    /// Test-Assembly nach oben ist stabiler als ein gezaehltes <c>../../../..</c>, das bei jeder
    /// Aenderung an der Ausgabe-Struktur still falsch wird.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>Debug oder Release — die Tests laufen in derselben Konfiguration wie die
    /// Binaries, die sie pruefen.</summary>
    private static string Configuration { get; } =
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    public static string LyrcPath => BinaryPath("Lyrc", "lyrc");
    public static string LyrvmPath => BinaryPath("Lyrvm", "lyrvm");
    public static string LyricPath => BinaryPath("Lyric.Cli", "lyric");

    /// <summary>Das Verzeichnis, in dem ein Binary samt seiner Abhaengigkeiten liegt — die
    /// Grundlage des Architektur-Tests.</summary>
    public static string OutputDirectory(string project) =>
        Path.Combine(RepositoryRoot, "src", project, "bin", Configuration, "net10.0");

    public static string Example(string name) => Path.Combine(RepositoryRoot, "examples", name);

    public static ToolResult Lyrc(params string[] args) => Run(LyrcPath, args);
    public static ToolResult Lyrvm(params string[] args) => Run(LyrvmPath, args);
    public static ToolResult Lyric(params string[] args) => Run(LyricPath, args);

    public static string LyrreplPath => BinaryPath("Lyrrepl", "lyrrepl");

    /// <summary>
    /// Faehrt ein Werkzeug und schreibt ihm etwas auf stdin — fuer die REPL, die anders nicht
    /// pruefbar waere.
    ///
    /// <para>Der Eingabestrom wird nach der letzten Zeile <b>geschlossen</b>. Ein EOF ist fuer
    /// die REPL das Ende (Ctrl+D), also beendet sie sich auch ohne ':quit' — ohne das Schliessen
    /// wartete der Test bis zum Timeout.</para>
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
    /// Wie <see cref="Run(string, string[])"/>, aber mit Umgebungsvariablen fuer <b>nur diesen</b>
    /// Kindprozess.
    ///
    /// <para>Nicht <c>Environment.SetEnvironmentVariable</c> im Testprozess: xUnit faehrt
    /// Testklassen parallel, und eine gesetzte Variable wirkte dann auf jeden gleichzeitig
    /// gestarteten Compiler mit. Genau daran ist dieses Projekt beim ersten Gesamtlauf haengen
    /// geblieben — isoliert gruen, zusammen rot. Geteilter veraenderlicher Zustand zwischen Tests
    /// ist die Ursache, ein Collection-Attribut waere nur das Pflaster.</para>
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

        // Erst lesen, dann warten: bei umgekehrter Reihenfolge blockiert ein Kind, sobald es mehr
        // schreibt, als in die Pipe passt.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new ToolResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>Ein Scratch-Pfad, der sich selbst aufraeumt.</summary>
    public static TemporaryFile Temp(string extension) => new(extension);

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

/// <summary>Eine temporaere Datei, die beim Verlassen des Scopes verschwindet.</summary>
public sealed class TemporaryFile(string extension) : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"lyric-test-{Guid.NewGuid():N}{extension}");

    public void Dispose()
    {
        try { File.Delete(Path); } catch (IOException) { /* nicht der Rede wert */ }
    }
}
