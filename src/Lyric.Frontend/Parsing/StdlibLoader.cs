using Lyric.AST;
using Lyric.Core;

namespace Lyric.Parsing;

/// <summary>
/// Findet Stdlib-Module auf der Platte und parst sie — die konkrete Seite von
/// <c>Compilation.ModuleLoader</c>.
///
/// <para>Liegt hier und nicht im Resolver, weil sie den Parser braucht: <c>Lyric.Resolver</c>
/// darf <c>Lyric.Parsing</c> nicht referenzieren. Der Resolver kennt nur den Delegaten.</para>
///
/// <para>Der Wurzelpfad ist ein Parameter, kein globaler Zustand — Tests zeigen auf das Repo,
/// die CLI auf das Verzeichnis neben dem Binary.</para>
/// </summary>
public static class StdlibLoader
{
    /// <summary>Umgebungsvariable, die den Stdlib-Pfad überschreibt. Für Entwicklung und Tests.</summary>
    public const string RootEnvironmentVariable = "LYRIC_STDLIB";

    /// <summary>Wo die Stdlib liegt: erst die Umgebungsvariable, sonst <c>stdlib/</c> neben dem
    /// Binary (dorthin kopiert das CLI-Projekt sie, das überlebt auch <c>dotnet publish</c>).</summary>
    public static string DefaultRoot()
    {
        var configured = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "stdlib");
    }

    /// <summary>
    /// Baut den Lader. Ein Modulpfad wird zum Dateipfad: <c>std.io.console</c> →
    /// <c>&lt;root&gt;/std/io/console.lyr</c> (ADR-012, dieselbe Ableitung wie bei User-Code, nur
    /// in die andere Richtung).
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
                // Existiert, aber nicht lesbar: wie „nicht gefunden" behandeln, damit daraus die
                // normale „unbekanntes Modul"-Diagnose wird statt einer Ausnahme aus dem Resolver.
                return null;
            }

            return (new Parser(sourceManager, id, diagnostics).ParseModule(), true);
        };
}
