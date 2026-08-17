using System.Reflection;
using Lyric.Core;

namespace Lyric.Cli;

/// <summary>
/// <c>lyric new</c> — writes a project that builds.
///
/// <para>Two shapes and two flags, as <c>zig init</c> and <c>cargo new</c> have them, rather than a
/// template system: with two variants a discovery mechanism is more machinery than content.</para>
///
/// <para>The templates are EMBEDDED, so nothing has to be found at runtime and no second directory
/// can go missing beside the binary the way a standard library can. They are real files in the
/// repository all the same, which is what lets the test suite compile them.</para>
///
/// <para>This is the one command the driver may own: it writes files, and compiles and executes
/// nothing.</para>
/// </summary>
public static class NewProject
{
    /// <summary>The identifier the project name replaces. It is a valid Lyric identifier, so the
    /// templates are compilable Lyric rather than text with holes in it.</summary>
    private const string Placeholder = "__name__";

    public static int Run(string[] args)
    {
        var name = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (name is null)
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.MissingArgument,
                "new: missing project name — try 'lyric new myapp'", ExitCodes.Usage);

        // The name becomes a module name and a directory name. A check here beats 'module 3d;'
        // failing in the compiler on a file nobody wrote by hand.
        if (!IsIdentifier(name))
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.UnknownCommand,
                $"'{name}' is not a usable project name: letters, digits and '_', not starting "
                + "with a digit", ExitCodes.Usage);

        var template = args.Contains("--lib") ? "lib" : "app";
        var target = Path.GetFullPath(name);

        // Refused rather than merged: a 'new' that writes into an existing project would overwrite
        // a build.lyr someone had already changed.
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.OutputUnwritable,
                $"{target} exists and is not empty", ExitCodes.Failure);

        try
        {
            foreach (var (relative, content) in Files(template))
            {
                var path = Path.Combine(target, Rename(relative, name));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content.Replace(Placeholder, name, StringComparison.Ordinal));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CliDiagnostics.Fail(Console.Error, CliDiagnostics.OutputUnwritable,
                $"{target}: {ex.Message}", ExitCodes.Failure);
        }

        Console.Out.WriteLine($"{target}: {template} project '{name}'");
        Console.Out.WriteLine(template == "app"
            ? $"  cd {name} && lyric build && lyric run src/main.lyr"
            : $"  cd {name} — point a program's sourceRoot here and import {name}");

        return ExitCodes.Success;
    }

    /// <summary>The template's files, by their path inside it.</summary>
    private static IEnumerable<(string Path, string Content)> Files(string template)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = $"templates/{template}/";

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var normalized = resource.Replace('\\', '/');
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) continue;

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (normalized[prefix.Length..], reader.ReadToEnd());
        }
    }

    /// <summary>
    /// The path a template file takes in the new project.
    ///
    /// <para><c>gitignore</c> becomes <c>.gitignore</c>: stored without the dot so it does not take
    /// effect in the repository that ships it. The placeholder applies to path segments too, which
    /// is what names a library's module file after the library.</para>
    /// </summary>
    private static string Rename(string relative, string name)
    {
        var renamed = relative.Replace(Placeholder, name, StringComparison.Ordinal);
        return Path.GetFileName(renamed) == "gitignore"
            ? Path.Combine(Path.GetDirectoryName(renamed) ?? string.Empty, ".gitignore")
            : renamed;
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0
        && !char.IsAsciiDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
