namespace Lyric.Core;

/// <summary>
/// Die Version der Toolchain — eine Zahl fuer <c>lyrc</c>, <c>lyrvm</c> und <c>lyric</c>.
///
/// <para>Seit ADR-017 gibt es drei Binaries, die aus einem Baum kommen und zusammen ausgeliefert
/// werden. Drei getrennte Versions-Konstanten waeren drei Gelegenheiten, beim Taggen eine davon
/// zu vergessen — und ein <c>lyrc</c>, das sich anders nennt als das <c>lyrvm</c> daneben, waere
/// beim Fehlerbericht schlicht irrefuehrend.</para>
///
/// <para>Bewusst getrennt von der <b>Format</b>-Version (<c>Lyric.Bytecode.Format</c>): ADR-013
/// entkoppelt beide ausdruecklich. Eine Fremd-Runtime hat ihre eigene Toolchain-Version und
/// trotzdem dieselbe Format-Version.</para>
/// </summary>
public static class ToolchainVersion
{
    /// <summary>
    /// Beim Release-Tag hochzuziehen — <b>und zwar hier UND in <c>Directory.Build.props</c></b>.
    /// MSBuild kann keine C#-Konstante lesen, also stehen beide da; ein Test vergleicht sie
    /// gegen das erzeugte Assembly-Attribut, statt die Doppelung wegzudiskutieren.
    ///
    /// <para>Eine dritte Stelle gibt es in <c>tooling/vscode-lyric/package.json</c>. Sie folgt
    /// nicht automatisch — eine Extension hat ihre eigene Veroeffentlichungskadenz, und ein
    /// Marketplace-Eintrag, dessen Nummer bei jedem Compiler-Patch springt, waere Laerm.</para>
    /// </summary>
    public const string Value = "0.9.0";
}
