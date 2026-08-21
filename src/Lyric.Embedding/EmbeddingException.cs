using Lyric.Core;

namespace Lyric.Embedding;

/// <summary>
/// A compilation that did not succeed.
///
/// <para>The diagnostics hang off it as data, not as rendered text, so a host can present them in
/// its own surface — with the place already resolved, because the manager that could resolve it
/// belongs to the compilation and does not outlive this throw.</para>
///
/// <para>A panic from a running script is not this exception; it is
/// <see cref="ScriptPanicException"/>.</para>
/// </summary>
public sealed class EmbeddingException : Exception
{
    internal EmbeddingException(string message, IReadOnlyList<ScriptDiagnostic> diagnostics)
        : base(message) => Diagnostics = diagnostics;

    /// <summary>Everything the compilation reported, in the same order as on the command
    /// line.</summary>
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; }
}
