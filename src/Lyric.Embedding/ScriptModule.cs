using Lyric.Bytecode;

namespace Lyric.Embedding;

/// <summary>
/// A compiled script: the loaded and validated bytes, ready to execute.
///
/// <para>A module without an entry point is a library, not a program; holding one without ever
/// running it is valid.</para>
///
/// <para>It carries both the bytes, which the host can write out, and the loaded module, which is
/// the validated form.</para>
/// </summary>
public sealed class ScriptModule
{
    internal ScriptModule(string name, byte[] bytes, BytecodeModule loaded, string? origin)
    {
        Name = name;
        Bytes = bytes;
        Loaded = loaded;
        Origin = origin;
    }

    /// <summary>
    /// The file this module came from, or <c>null</c> for source held in memory.
    ///
    /// <para><see cref="ScriptInstance.Reload"/> requires it: reloading means reading the same
    /// source again.</para>
    /// </summary>
    public string? Origin { get; }

    /// <summary>The module name it was compiled under.</summary>
    public string Name { get; }

    /// <summary>The <c>.lyrbc</c> bytes. Writable to disk and readable by any runtime.</summary>
    public byte[] Bytes { get; }

    /// <summary>Whether this module has an entry point. Without one <see cref="LangVm.Run"/> is
    /// an error.</summary>
    public bool HasEntryPoint => Loaded.Start is not null;

    private ModuleAttributes? _attributes;

    /// <summary>
    /// What the module says about itself and its declarations — BEFORE it is instantiated.
    ///
    /// <para>That order matters for foreign bytes: a host reads the module's own rows
    /// (<see cref="ModuleAttributes.OnModule"/>) and decides whether to instantiate at all.</para>
    /// </summary>
    public ModuleAttributes Attributes => _attributes ??= ModuleAttributes.Of(Loaded);

    internal BytecodeModule Loaded { get; }
}
