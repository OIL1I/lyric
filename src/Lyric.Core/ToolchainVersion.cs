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
    /// <summary>Beim Release-Tag hochzuziehen.</summary>
    public const string Value = "0.0.1-dev";
}
