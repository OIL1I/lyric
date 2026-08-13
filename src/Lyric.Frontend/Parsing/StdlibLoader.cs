using Lyric.AST;
using Lyric.Core;

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
    /// Builds the loader. A module path becomes a file path: <c>std.io.console</c> →
    /// <c>&lt;root&gt;/std/io/console.lyr</c> — the same derivation as for user code, in the other
    /// direction.
    /// </summary>
    public static Func<string[], (Module Ast, bool IsNative)?> ForRoot(
        string root, SourceManager sourceManager, DiagnosticEngine diagnostics) =>
        path =>
        {
            var file = Path.Combine(root, Path.Combine(path)) + ".lyr";
            if (!File.Exists(file)) return null;

            FileId id;
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

            return (new Parser(sourceManager, id, diagnostics).ParseModule(), true);
        };
}
