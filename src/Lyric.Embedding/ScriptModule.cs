using Lyric.Bytecode;

namespace Lyric.Embedding;

/// <summary>
/// Ein uebersetztes Skript: die geladenen und validierten Bytes, bereit zum Ausfuehren.
///
/// <para>Der Host haelt das Ding und reicht es an <see cref="LangVm.Run"/> — oder legt es weg und
/// fuehrt es nie aus. Beides ist gueltig; ein <c>.lyrbc</c> ohne Einstiegspunkt ist eine
/// Bibliothek und kein Programm (siehe <c>examples/embedded.lyr</c>).</para>
///
/// <para><b>Warum die Bytes UND das geladene Modul.</b> Die Bytes sind das, was der Host
/// wegschreiben kann — sie sind das Format aus ADR-013, und der Sinn eines
/// <c>Compile</c>-Schritts neben <c>RunScript</c> ist, sie einmal zu erzeugen und mehrfach zu
/// benutzen. Das geladene Modul daneben ist die validierte Form; es zweimal zu lesen waere
/// Arbeit ohne Aussage.</para>
/// </summary>
public sealed class ScriptModule
{
    internal ScriptModule(string name, byte[] bytes, BytecodeModule loaded)
    {
        Name = name;
        Bytes = bytes;
        Loaded = loaded;
    }

    /// <summary>Der Modulname, unter dem es uebersetzt wurde.</summary>
    public string Name { get; }

    /// <summary>Die <c>.lyrbc</c>-Bytes. Wegschreibbar, von jeder Runtime lesbar
    /// (<c>Bytecode.md</c> §9).</summary>
    public byte[] Bytes { get; }

    /// <summary>Hat dieses Modul einen Einstiegspunkt? Ohne ihn ist <see cref="LangVm.Run"/> ein
    /// Fehler — es gibt nichts auszufuehren.</summary>
    public bool HasEntryPoint => Loaded.Start is not null;

    internal BytecodeModule Loaded { get; }
}
