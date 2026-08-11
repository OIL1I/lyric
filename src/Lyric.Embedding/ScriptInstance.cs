using Lyric.Bytecode;
using Lyric.Vm;

namespace Lyric.Embedding;

/// <summary>
/// Ein geladenes, initialisiertes Skript, aus dem der Host Funktionen ruft (M10/E2).
///
/// <para><b>Warum das ein eigenes Ding ist und nicht eine Methode auf <see cref="LangVm"/>.</b>
/// Die ROADMAP skizzierte <c>vm.Call&lt;T&gt;(name, args)</c> — als haette eine VM genau ein
/// Skript. Sobald sie zwei hat (ein Host mit zwei Mods ist der Normalfall), muesste <c>Call</c>
/// raten, welches gemeint ist, oder es gaebe ein implizites „aktuelles Skript". Beides ist die
/// Sorte verborgener Zustand, die genau dann beisst, wenn zwei Mods sich gegenseitig
/// ueberschreiben.</para>
///
/// <para><b>Eine Instanz IST der Zustand.</b> Der Initialisierer der Modul-Konstanten laeuft beim
/// Erzeugen genau einmal (Bytecode.md §Globals), und was er hinterlaesst, ueberlebt jeden Aufruf.
/// Zwei Instanzen desselben Moduls teilen nichts. Damit faellt ADR-025s Reload-Zusage („der
/// Initialisierer laeuft neu") in E5 von selbst heraus: neu laden heisst neue Instanz.</para>
/// </summary>
public sealed class ScriptInstance
{
    private readonly LoadedProgram _program;
    private readonly string _prefix;

    internal ScriptInstance(ScriptModule module, LoadedProgram program)
    {
        Module = module;
        _program = program;
        _prefix = module.Name + ".";
    }

    /// <summary>Woraus diese Instanz entstanden ist.</summary>
    public ScriptModule Module { get; }

    /// <summary>Kennt dieses Skript eine <c>pub</c>-Funktion dieses Namens?</summary>
    public bool Defines(string function) => _program.IndexOfFunction(_prefix + function) >= 0;

    /// <summary>
    /// Ruft eine Funktion des Skripts und liefert ihr Ergebnis.
    /// </summary>
    /// <param name="function">Der <b>unqualifizierte</b> Name. Der Modulname kommt davor —
    /// dieselbe Instanz weiss ihn, und ohne die Qualifizierung faende ein Aufruf von
    /// <c>length</c> ebenso gut <c>std.string.length</c>, das mit im Modul liegt.</param>
    /// <exception cref="ScriptException">Die Funktion gibt es nicht, die Aritaet stimmt nicht,
    /// oder ein Wert passt nicht ueber die Grenze.</exception>
    /// <exception cref="ScriptPanicException">Das Skript hat seinen eigenen Vertrag gebrochen.
    /// </exception>
    public TResult Call<TResult>(string function, params object?[] arguments)
    {
        var (index, signature) = Resolve(function, arguments.Length);
        var marshalled = MarshalArguments(function, signature, arguments);

        var produced = Invoke(index, marshalled);
        return Marshal.FromLyric<TResult>(produced, signature.ReturnType,
            $"the result of '{function}'");
    }

    /// <summary>Wie <see cref="Call{TResult}"/>, aber fuer eine Funktion ohne Rueckgabewert.
    ///
    /// <para>Getrennt, weil <c>Call&lt;void&gt;</c> in C# nicht schreibbar ist — und ein
    /// <c>Call&lt;object&gt;</c>, das <c>null</c> liefert, waere von einem echten Ergebnis nicht
    /// zu unterscheiden.</para></summary>
    public void CallVoid(string function, params object?[] arguments)
    {
        var (index, signature) = Resolve(function, arguments.Length);
        Invoke(index, MarshalArguments(function, signature, arguments));
    }

    private (int Index, BytecodeFunction Signature) Resolve(string function, int argumentCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(function);

        var index = _program.IndexOfFunction(_prefix + function);
        if (index < 0)
            throw new ScriptException("LYR-EMB0006",
                $"'{Module.Name}' has no function '{function}'", null);

        var signature = _program.Module.Functions[index];
        if (signature.ParamCount != argumentCount)
            throw new ScriptException("LYR-EMB0007",
                $"'{function}' takes {signature.ParamCount} argument(s), got {argumentCount}",
                null);

        return (index, signature);
    }

    private static LyrValue[] MarshalArguments(string function, BytecodeFunction signature,
        object?[] arguments)
    {
        var values = new LyrValue[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
            values[i] = Marshal.ToLyric(arguments[i], signature.SlotTypes[i],
                $"argument {i + 1} of '{function}'");
        return values;
    }

    private LyrValue Invoke(int index, LyrValue[] arguments)
    {
        try
        {
            return _program.Invoke(index, arguments);
        }
        catch (LyricPanic panic)
        {
            throw new ScriptPanicException(panic.Code, panic.Message, panic);
        }
        catch (LyricRuntimeException runtime)
        {
            throw new ScriptException(runtime.Code, runtime.Message, runtime);
        }
    }
}
