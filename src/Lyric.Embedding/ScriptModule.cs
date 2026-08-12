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

    internal BytecodeModule Loaded { get; }
}
