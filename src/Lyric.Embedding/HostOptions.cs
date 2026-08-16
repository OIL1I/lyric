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

    /// <summary>
    /// Directories whose modules may declare functions without a body, keyed by the module path
    /// segment they own: <c>["engine"] = "…/sdk"</c> lets a script write <c>import engine.input</c>
    /// and read the declarations from <c>…/sdk/engine/input.lyr</c>.
    ///
    /// <para>For an SDK whose surface is large enough that generating it through
    /// <see cref="LangVm.RegisterFunction"/> means keeping the same signatures in two places. The
    /// implementations still come from the host, through
    /// <see cref="LangVm.RegisterNative"/>.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string>? NativeRoots { get; init; }

    /// <summary>Where a script writes. Defaults to <see cref="TextWriter.Null"/>.</summary>
    public TextWriter? Output { get; init; }

    /// <inheritdoc cref="Output"/>
    public TextWriter? Error { get; init; }
}
