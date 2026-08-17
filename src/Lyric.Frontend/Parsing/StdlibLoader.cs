using Lyric.AST;
using Lyric.Core;
using Lyric.Resolver;

namespace Lyric.Parsing;

/// <summary>
/// Finds stdlib modules on disk and parses them — the concrete side of the module loader delegate.
///
/// <para>Lives here rather than in the resolver because it needs the parser: <c>Lyric.Resolver</c>
/// must not reference <c>Lyric.Parsing</c>. The resolver knows only the delegate.</para>
///
/// <para>The root path is a parameter rather than global state — tests point at the repository, the
/// CLI at the directory next to the binary.</para>
/// </summary>
public static class StdlibLoader
{
    /// <summary>The environment variable that overrides the stdlib path.</summary>
    public const string RootEnvironmentVariable = "LYRIC_STDLIB";

    /// <summary>Where the stdlib lives: the environment variable first, otherwise <c>stdlib/</c>
    /// next to the binary, where the CLI project copies it and where it survives
    /// <c>dotnet publish</c>.</summary>
    public static string DefaultRoot()
    {
        var configured = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "stdlib");
    }

    /// <summary>
    /// Builds the loader for the standard library. A module path becomes a file path:
    /// <c>std.io.console</c> → <c>&lt;root&gt;/std/io/console.lyr</c> — the same derivation as for
    /// user code, in the other direction.
    /// </summary>
    public static Func<string[], LoadedModule?> ForRoot(
        string root, SourceManager sourceManager, DiagnosticEngine diagnostics,
        IReadOnlyDictionary<string, string>? overlay = null) =>
        Build(root, sourceManager, diagnostics, native: true, overlay);

    /// <summary>
    /// Builds the loader for a program's OWN modules, rooted at the directory of its entry file.
    ///
    /// <para>The only difference to <see cref="ForRoot"/> is that these modules are NOT native: a
    /// function without a body is an error here rather than an import declaration.
    /// <c>Compilation.IsNative</c> follows the ORIGIN of a module and not its content, and this is
    /// the origin that decides.</para>
    /// </summary>
    public static Func<string[], LoadedModule?> ForProject(
        string root, SourceManager sourceManager, DiagnosticEngine diagnostics,
        IReadOnlyDictionary<string, string>? overlay = null) =>
        Build(root, sourceManager, diagnostics, native: false, overlay);

    private static Func<string[], LoadedModule?> Build(
        string root, SourceManager sourceManager, DiagnosticEngine diagnostics, bool native,
        IReadOnlyDictionary<string, string>? overlay) =>
        path =>
        {
            var file = Path.Combine(root, Path.Combine(path)) + ".lyr";

            FileId id;

            // The overlay wins over the disk, and applies even when there is no file yet: an editor
            // holds the authoritative text of everything it has open, including a module that has
            // never been saved.
            if (overlay is not null && overlay.TryGetValue(Path.GetFullPath(file), out var text))
            {
                id = sourceManager.AddVirtual(file, text);
            }
            else
            {
                if (!File.Exists(file)) return null;

                try
                {
                    id = sourceManager.AddFromDisk(file);
                }
                catch
                {
                    // Present but unreadable: treated as "not found", so it turns into the ordinary
                    // "unknown module" diagnostic instead of an exception out of the resolver.
                    return null;
                }
            }

            var parsed = ParsedModule.Parse(sourceManager, id, diagnostics);
            return new LoadedModule(parsed.Ast, native, parsed.Documentation);
        };
}
