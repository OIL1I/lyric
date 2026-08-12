namespace Lyric.Core;

/// <summary>
/// The process exit codes of the toolchain, normative for every runtime.
///
/// <para>They live in <c>Lyric.Core</c>, the only project <c>lyrc</c>, <c>lyrvm</c> and
/// <c>lyric</c> share, so the numbers exist once.</para>
///
/// <para>A program returning <c>101</c> itself is indistinguishable from a panic; that is
/// unavoidable once both travel through one byte channel.</para>
/// </summary>
public static class ExitCodes
{
    /// <summary>Success.</summary>
    public const int Success = 0;

    /// <summary>Load, validation, compile or IO error: the program never started.</summary>
    public const int Failure = 1;

    /// <summary>Wrong command-line invocation. Separate from <see cref="Failure"/> so a caller can
    /// tell a misuse from a broken file.</summary>
    public const int Usage = 2;

    /// <summary>A panic. Not 1, so a caller can tell it from a regular <c>return 1;</c>.
    /// </summary>
    public const int Panic = 101;
}
