using Lyric.Core;

namespace Lyric.Embedding;

/// <summary>
/// What a host settles when it creates a <see cref="LangVm"/>.
///
/// <para>The default is <see cref="Capability.None"/>: a script reaches nothing until the host
/// grants it.</para>
/// </summary>
public sealed record HostOptions
{
    /// <summary>What scripts of this VM may reach. Default: nothing.</summary>
    public Capability Capabilities { get; init; } = Capability.None;

    /// <summary>Where the standard library lives. <c>null</c> takes the directory next to the
    /// binary.</summary>
    public string? StdlibRoot { get; init; }

    /// <summary>Where a script writes. Defaults to <see cref="TextWriter.Null"/>.</summary>
    public TextWriter? Output { get; init; }

    /// <inheritdoc cref="Output"/>
    public TextWriter? Error { get; init; }
}
