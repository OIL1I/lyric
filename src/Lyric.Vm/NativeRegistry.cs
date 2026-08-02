using System.Globalization;
using Lyric.Bytecode;

namespace Lyric.Vm;

/// <summary>
/// Die nativen Implementierungen hinter den Import-Deklarationen der Stdlib.
///
/// <para>Gebunden wird <b>symbolisch über den Namen</b>, beim Laden (ADR-013, WASM-Modell): das
/// Modul nennt <c>std.io.console.println</c> samt Signatur, die Registry liefert den Delegaten.
/// Fehlt einer oder passt die Signatur nicht, wird das Modul abgelehnt — nicht erst beim Aufruf.</para>
///
/// <para>Das ist derselbe Seam, den <b>M10s <c>RegisterFunction</c></b> und <b>ADR-007s
/// Capabilities</b> benutzen werden: der Host entscheidet, was ein Script sehen darf. In M6 ist die
/// CLI der Host und registriert die Built-ins.</para>
/// </summary>
public sealed class NativeRegistry
{
    private readonly Dictionary<string, Native> _natives = new(StringComparer.Ordinal);

    private sealed record Native(
        TypeTag[] ParamTypes, TypeTag ReturnType, Func<LyrValue[], LyrValue> Implementation);

    public void Register(string name, TypeTag[] paramTypes, TypeTag returnType,
        Func<LyrValue[], LyrValue> implementation) =>
        _natives[name] = new Native(paramTypes, returnType, implementation);

    /// <summary>Ein gebundener Import: Implementierung plus das, was die Aufrufstelle wissen muss.
    /// Arität und Rückgabe stehen hier, damit der Interpreter im heißen Pfad nichts nachschlagen
    /// muss.</summary>
    public sealed record BoundNative(int Arity, bool ReturnsValue,
        Func<LyrValue[], LyrValue> Implementation);

    /// <summary>Bindet alle Imports eines Moduls. Wirft, sobald einer fehlt oder anders aussieht,
    /// als das Modul erwartet — Validierung beim Laden statt beim Aufruf.</summary>
    public BoundNative[] Bind(BytecodeModule module)
    {
        var bound = new BoundNative[module.Imports.Count];

        for (var i = 0; i < module.Imports.Count; i++)
        {
            var import = module.Imports[i];
            if (!_natives.TryGetValue(import.Name, out var native))
                throw new LyricRuntimeException(VmDiagnostics.ImportsNotBound,
                    $"no native implementation for '{import.Name}'");

            if (!native.ParamTypes.SequenceEqual(import.ParamTypes) ||
                native.ReturnType != import.ReturnType)
                throw new LyricRuntimeException(VmDiagnostics.ImportsNotBound,
                    $"native '{import.Name}' has a different signature than the module expects");

            bound[i] = new BoundNative(import.ParamTypes.Count,
                import.ReturnType != TypeTag.Void, native.Implementation);
        }

        return bound;
    }

    /// <summary>
    /// Die eingebauten Natives der Standalone-CLI. <paramref name="output"/> und
    /// <paramref name="error"/> sind Parameter, damit Tests die Ausgabe einsammeln können, ohne
    /// <c>Console</c> umzubiegen.
    /// </summary>
    public static NativeRegistry CreateDefault(TextWriter output, TextWriter error)
    {
        var registry = new NativeRegistry();
        var str = new[] { TypeTag.String };

        registry.Register("std.io.console.print", str, TypeTag.Void,
            args => { output.Write(args[0].AsString); return default; });
        registry.Register("std.io.console.println", str, TypeTag.Void,
            // Immer '\n', nie Environment.NewLine: die Ausgabe eines Lyric-Programms darf nicht
            // vom Betriebssystem abhängen, sonst sind Golden-Tests auf zwei Runnern verschieden.
            args => { output.Write(args[0].AsString); output.Write('\n'); return default; });
        registry.Register("std.io.console.eprintln", str, TypeTag.Void,
            args => { error.Write(args[0].AsString); error.Write('\n'); return default; });

        registry.Register("std.string.concat", new[] { TypeTag.String, TypeTag.String },
            TypeTag.String, args => LyrValue.FromString(args[0].AsString + args[1].AsString));

        // Invariante Kultur: '3.5' und nicht '3,5' — dieselbe .lyrbc-Datei muss auf jeder Maschine
        // dieselbe Ausgabe erzeugen.
        registry.Register("std.string.fromInt", new[] { TypeTag.I64 }, TypeTag.String,
            args => LyrValue.FromString(args[0].AsI64.ToString(CultureInfo.InvariantCulture)));
        registry.Register("std.string.fromFloat", new[] { TypeTag.F64 }, TypeTag.String,
            args => LyrValue.FromString(args[0].AsF64.ToString("R", CultureInfo.InvariantCulture)));
        registry.Register("std.string.fromBool", new[] { TypeTag.Bool }, TypeTag.String,
            args => LyrValue.FromString(args[0].AsBool ? "true" : "false"));
        registry.Register("std.string.fromChar", new[] { TypeTag.Char }, TypeTag.String,
            args => LyrValue.FromString(char.ConvertFromUtf32((int)args[0].Bits)));

        return registry;
    }
}
