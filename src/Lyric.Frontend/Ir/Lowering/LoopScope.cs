namespace Lyric.Ir.Lowering;

/// <summary>
/// Sprungziele der umgebenden Schleife. Als Stack geführt, weil <c>break</c>/<c>continue</c> immer
/// die <b>innerste</b> Schleife meinen (Lyric hat keine Labels, Sprache.md §5).
///
/// <para><c>continue</c> springt nicht an den Body-Anfang, sondern dorthin, wo die Bedingung neu
/// geprüft wird: bei <c>while</c> ist das der Cond-Block, bei <c>do-while</c> der Cond-Block am
/// Ende. Deshalb zwei getrennte Ziele statt eines Body-Zeigers.</para>
/// </summary>
internal readonly record struct LoopScope(BlockId ContinueTarget, BlockId BreakTarget);
