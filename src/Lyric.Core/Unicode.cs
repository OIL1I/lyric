namespace Lyric.Core;

/// <summary>
/// What a <c>char</c> may be.
///
/// <para>The rule is needed in two assemblies that cannot reference each other: the sema checks a
/// <c>char</c> literal while compiling, the VM checks every computed result while running.
/// <c>Lyric.Core</c> is the only project both see, so the bound exists once.</para>
/// </summary>
public static class Unicode
{
    /// <summary>The largest valid code point.</summary>
    public const long MaxCodepoint = 0x10FFFF;

    /// <summary>First and last surrogate. They are halves of a UTF-16 pair and not a character on
    /// their own, so not a valid <c>char</c>.</summary>
    public const long FirstSurrogate = 0xD800;
    public const long LastSurrogate = 0xDFFF;

    /// <summary>Is this a Unicode code point?</summary>
    /// <remarks>Takes <c>long</c> rather than <c>uint</c>, so a negative intermediate result
    /// (<c>'a' - 1000</c>) is seen as such.</remarks>
    public static bool IsCodepoint(long value) =>
        value is >= 0 and <= MaxCodepoint
        && value is < FirstSurrogate or > LastSurrogate;

    /// <summary>Why a value is not one, shared by the diagnostic and the panic message.</summary>
    public static string DescribeRange() =>
        $"valid: 0..0x{MaxCodepoint:X}, excluding the surrogate range " +
        $"0x{FirstSurrogate:X}..0x{LastSurrogate:X}";
}
