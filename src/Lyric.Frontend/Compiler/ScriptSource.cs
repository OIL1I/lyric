using Lyric.Core;

namespace Lyric.Compiler;

/// <summary>
/// Woher der Quelltext des Einstiegsmoduls kommt: von der Platte oder aus dem Speicher.
///
/// <para><b>Warum es das gibt.</b> Die Pipeline war bis 2026-08-11 pfadgebunden, und das war
/// keine Bequemlichkeit: <c>Sprache.md</c> §2.1 leitet den Modulnamen aus dem Dateipfad ab. Ein
/// Host, der ein Skript aus dem Speicher uebersetzt (M10, <c>LangVm.Compile</c>), hat keinen —
/// <b>also muss er den Namen nennen</b>. Genau das tut <see cref="SourceManager.AddVirtual"/>,
/// seit M3 der Weg der Testsuite.</para>
///
/// <para><b>Eine Naht, kein zweiter Weg.</b> Der Unterschied zwischen beiden Herkuenften ist
/// genau ein Schritt — das Beschaffen der <see cref="FileId"/>. Alles danach ist identisch. Eine
/// zweite Pipeline daneben waere derselbe Fehler, den ADR-017 bereits einmal aufgeraeumt hat:
/// als <c>run</c>, <c>lower</c> und <c>check</c> je eine eigene Kopie des Vorspanns hatten,
/// verdrahtete nur eine davon den Modul-Lader.</para>
/// </summary>
public sealed class ScriptSource
{
    private readonly string? _path;
    private readonly string? _text;

    private ScriptSource(string displayName, string? moduleName, string? path, string? text)
    {
        DisplayName = displayName;
        ModuleName = moduleName;
        _path = path;
        _text = text;
    }

    /// <summary>Der Name fuer Diagnosen und die Fortschrittsanzeige.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// Wie das Modul <b>heisst</b> — nicht, wie es angezeigt wird.
    ///
    /// <para>Die Unterscheidung ist noetig und war beim ersten Anlauf nicht da: der Anzeigename
    /// landete in den Diagnosen, die Modul-Identitaet blieb <c>main</c>. Ein Host, der zwei Mods
    /// laedt, haette zwei Module namens <c>main</c> gehabt — und ein Aufruf ueber den Namen
    /// (M10/E2) faende die Funktion des falschen. Gefunden vom ersten Test, der eine Funktion
    /// beim Namen rief.</para>
    ///
    /// <para><c>null</c> bei einer Datei: dort leitet §2.1 den Namen aus dem Pfad ab, und das ist
    /// die Regel, die gelten soll.</para>
    /// </summary>
    public string? ModuleName { get; }

    /// <summary>
    /// Eine Datei auf der Platte.
    /// </summary>
    /// <param name="moduleName">Wie das Modul heissen soll. <c>null</c> ueberlaesst es der
    /// Voreinstellung des Resolvers (<c>main</c>) — das tut die CLI, deren Ausgabe seit M5 so
    /// aussieht. Ein Host nennt ihn, weil er ihn spaeter zum Aufrufen braucht: bis 2026-08-11
    /// meldete <c>LangVm.CompileFile</c> den Dateinamen, waehrend das Modul <c>main</c> hiess, und
    /// ein <c>Call</c> darauf fand nichts. Aufgefallen erst beim Bau von <c>Reload</c> (E5) —
    /// vorher hatte kein Test eine Datei uebersetzt UND daraus gerufen.</param>
    public static ScriptSource FromDisk(string path, string? moduleName = null) =>
        new(Path.GetFileName(path), moduleName, path, null);

    /// <summary>
    /// Quelltext aus dem Speicher unter einem vom Aufrufer gewaehlten Namen.
    ///
    /// <para>Der Name ist <b>Pflicht</b> und keine Voreinstellung wert: zwei Skripte ohne Pfad
    /// unter demselben Namen kollidierten still, und ob zwei Mods dasselbe Modul sind, weiss nur
    /// der Host.</para>
    /// </summary>
    public static ScriptSource FromText(string moduleName, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(text);
        return new ScriptSource(moduleName, moduleName, null, text);
    }

    /// <summary>Legt den Quelltext im <see cref="SourceManager"/> ab. <c>null</c>, wenn die Datei
    /// nicht lesbar war — die Diagnose steht dann in <paramref name="diagnostics"/>.</summary>
    internal FileId? Open(SourceManager sources, DiagnosticEngine diagnostics)
    {
        if (_text is not null) return sources.AddVirtual(DisplayName, _text);

        try
        {
            return sources.AddFromDisk(_path!);
        }
        catch
        {
            diagnostics.Report(CliDiagnostics.FileUnreadable, Severity.Error, default,
                $"failed to read file: {_path}");
            return null;
        }
    }
}
