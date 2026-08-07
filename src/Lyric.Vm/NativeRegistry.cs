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
        TypeTag[] ParamTypes, TypeTag ReturnType, Func<LyrValue[], LyrValue> Implementation,
        TypeTag? ReturnElement = null);

    public void Register(string name, TypeTag[] paramTypes, TypeTag returnType,
        Func<LyrValue[], LyrValue> implementation) =>
        _natives[name] = new Native(paramTypes, returnType, implementation);

    /// <summary>Ein Native, das ein <c>T[]</c> liefert (<c>split</c>, <c>toChars</c>).
    ///
    /// <para>Das erweitert die Regel „Natives nehmen nur Skalare" bewusst um <b>einen</b> Fall,
    /// und die Linie bleibt scharf: ein Array hat — anders als eine Klasse — <b>kein Layout</b>.
    /// Es ist eine homogene Sequenz, ihr Elementtyp steht im Import, und der Host muss über die
    /// Felderordnung eines Moduls nichts wissen. Objekte bleiben deshalb weiterhin draußen.</para>
    ///
    /// <para><paramref name="element"/> wird beim Binden mitgeprüft. Ohne ihn wären
    /// <c>string[]</c> und <c>char[]</c> ununterscheidbar, und die Load-Zeit-Validierung
    /// (ADR-013) hätte genau dort ein Loch, wo sie am billigsten zu haben ist.</para></summary>
    public void RegisterArrayReturning(string name, TypeTag[] paramTypes, TypeTag element,
        Func<LyrValue[], LyrValue> implementation) =>
        _natives[name] = new Native(paramTypes, TypeTag.Array, implementation, element);

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

            // Natives sind Host-Code und nehmen nur Skalare: ein Objekt-Layout ist Sache des
            // Moduls, nicht des Hosts. Der Tag-Vergleich genügt deshalb — und lehnt eine
            // Referenz-Signatur automatisch ab, weil keine Native je Ref deklariert.
            if (!native.ParamTypes.SequenceEqual(import.ParamTypes.Select(p => p.Tag)) ||
                native.ReturnType != import.ReturnType.Tag)
                throw new LyricRuntimeException(VmDiagnostics.ImportsNotBound,
                    $"native '{import.Name}' has a different signature than the module expects");

            // Bei einem Array-Rueckgabetyp genuegt der Tag nicht: 'string[]' und 'char[]' tragen
            // beide TypeTag.Array.
            if (native.ReturnElement is { } expected
                && import.ReturnType.Element?.Tag != expected)
                throw new LyricRuntimeException(VmDiagnostics.ImportsNotBound,
                    $"native '{import.Name}' returns a different array element type than the "
                    + "module expects");

            bound[i] = new BoundNative(import.ParamTypes.Count,
                import.ReturnType.Tag != TypeTag.Void, native.Implementation);
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

        // 'ab' * 3 (Sprache.md §6.5). Ein negativer Faktor liefert den leeren String statt zu
        // werfen: die Spec kennt dafuer keinen Fehlerfall, und 'string.Concat' mit negativer
        // Anzahl waere eine .NET-Ausnahme mitten in einem Lyric-Programm.
        registry.Register("std.string.repeat", new[] { TypeTag.String, TypeTag.I64 },
            TypeTag.String, args => LyrValue.FromString(
                args[1].AsI64 <= 0 ? string.Empty
                    : string.Concat(Enumerable.Repeat(args[0].AsString, (int)args[1].AsI64))));

        // panic ist nicht catchbar (§9) und kehrt nie zurueck — deshalb ein Wurf und kein
        // Rueckgabewert. Den Backtrace haengt die Schleife an, die den Frame-Stack haelt.
        registry.Register("std.core.panic", str, TypeTag.Void,
            args => throw new LyricPanic(VmDiagnostics.Panicked, args[0].AsString));

        // Ein 'resume' auf eine durchgelaufene Coroutine (Sprache.md §8). Bis Throwable-Typen
        // aus der Stdlib kommen (M8) ist das ein Panic und kein fangbarer Fehler — die Meldung
        // sagt dasselbe, die Abweichung steht in STATUS.
        registry.Register("std.core.coroutineEnded", Array.Empty<TypeTag>(), TypeTag.Void,
            _ => throw new LyricPanic(VmDiagnostics.Panicked,
                "resume on a coroutine that has already finished"));

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

        // --- Abfragen (M8/S2) -----------------------------------------------------------
        //
        // ALLE Positionen und Laengen zaehlen CODEPOINTS, nicht UTF-16-Einheiten und nicht
        // Bytes. Das ist keine Geschmacksfrage: Sprache.md 4 sagt "char = ein Unicode-
        // Codepoint", und eine Laenge, die etwas anderes zaehlt als die Iteration liefert,
        // waere ein Widerspruch im eigenen Typsystem. C# macht genau diesen Fehler — dort ist
        // ein 'char' eine UTF-16-Einheit, und die Laenge eines Emoji ist 2 —, was dort nur
        // historisch erklaerbar ist (UCS-2 war einmal breit genug fuer alles).
        //
        // Der Preis ist O(n) statt O(1). Er ist bezahlbar, weil es kein 's[i]' gibt: der
        // quadratische Zugriff in einer Indexschleife ist damit gar nicht schreibbar.

        registry.Register("std.string.length", str, TypeTag.I64,
            args => LyrValue.FromI64(CodepointCount(args[0].AsString)));

        registry.Register("std.string.charAt", new[] { TypeTag.String, TypeTag.I64 }, TypeTag.Char,
            args => LyrValue.FromBits((ulong)CodepointAt(args[0].AsString, args[1].AsI64)));

        registry.Register("std.string.substring",
            new[] { TypeTag.String, TypeTag.I64, TypeTag.I64 }, TypeTag.String,
            args => LyrValue.FromString(Substring(args[0].AsString, args[1].AsI64, args[2].AsI64)));

        registry.Register("std.string.indexOf", new[] { TypeTag.String, TypeTag.String },
            TypeTag.I64, args => LyrValue.FromI64(IndexOf(args[0].AsString, args[1].AsString)));

        registry.Register("std.string.contains", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool,
            args => LyrValue.FromBool(args[0].AsString.Contains(args[1].AsString, StringComparison.Ordinal)));

        registry.Register("std.string.startsWith", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool,
            args => LyrValue.FromBool(args[0].AsString.StartsWith(args[1].AsString, StringComparison.Ordinal)));

        registry.Register("std.string.endsWith", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool,
            args => LyrValue.FromBool(args[0].AsString.EndsWith(args[1].AsString, StringComparison.Ordinal)));

        registry.Register("std.string.trim", str, TypeTag.String,
            args => LyrValue.FromString(args[0].AsString.Trim()));

        // Ordinal statt kulturabhaengig: dasselbe Programm muss auf jeder Maschine dasselbe
        // liefern. Die tuerkische Locale macht aus einem kleinen i ein grosses I mit Punkt — ein
        // Klassiker, der Programme nur auf manchen Rechnern kaputtmacht.
        registry.Register("std.string.toUpper", str, TypeTag.String,
            args => LyrValue.FromString(args[0].AsString.ToUpperInvariant()));

        registry.Register("std.string.toLower", str, TypeTag.String,
            args => LyrValue.FromString(args[0].AsString.ToLowerInvariant()));

        registry.RegisterArrayReturning("std.string.split",
            new[] { TypeTag.String, TypeTag.String }, TypeTag.String,
            args => Split(args[0].AsString, args[1].AsString));

        // Der Anker fuer 'for (c in s)'. Einmal O(n) statt n-mal O(n): ein Iterator, der
        // 'charAt' riefe, waere quadratisch, und das sieht man einem Einzeiler nicht an.
        registry.RegisterArrayReturning("std.string.toChars", str, TypeTag.Char,
            args => ToChars(args[0].AsString));

        // --- std.os (permission-gated, ADR-007) -----------------------------------------
        //
        // Die Natives sind IMMER registriert. Die Capability entscheidet, ob ein Modul, das sie
        // braucht, ueberhaupt geladen wird — nicht, ob die Funktion existiert. Das ist die
        // saubere Trennung: der Host konfiguriert eine Richtlinie, keine Funktionsliste.
        registry.Register("std.os.platform", Array.Empty<TypeTag>(), TypeTag.String,
            _ => LyrValue.FromString(
                OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsLinux() ? "linux"
                : OperatingSystem.IsMacOS() ? "macos"
                : "unknown"));

        // --- std.fmt (M8/S3) ------------------------------------------------------------
        //
        // Die Spec-Sprache ist die von .NET, wie Sprache.md 2.2 es verlangt, und sie wird
        // unveraendert durchgereicht: N2, F3, D5, X, E2, P1. Lyric erfindet keine eigene
        // Notation daneben — eine zweite waere ein zweiter Mechanismus fuer dieselbe Sache.
        //
        // IMMER invariant. Eine Zahl, die unter deutscher Locale "1.234,57" und unter
        // englischer "1,234.57" wird, ist kein Formatierungsdetail, sondern ein Programm, das
        // sich je nach Rechner anders verhaelt. Dieselbe Entscheidung wie bei toUpper/toLower.
        registry.Register("std.fmt.formatInt", new[] { TypeTag.I64, TypeTag.String },
            TypeTag.String, args => Formatted(args[0].AsI64, args[1].AsString));

        registry.Register("std.fmt.formatFloat", new[] { TypeTag.F64, TypeTag.String },
            TypeTag.String, args => Formatted(args[0].AsF64, args[1].AsString));

        registry.Register("std.fmt.formatBool", new[] { TypeTag.Bool, TypeTag.String },
            TypeTag.String, args => LyrValue.FromString(Padded(args[0].AsBool ? "true" : "false",
                args[1].AsString)));

        registry.Register("std.fmt.formatChar", new[] { TypeTag.Char, TypeTag.String },
            TypeTag.String, args => LyrValue.FromString(Padded(
                char.ConvertFromUtf32((int)args[0].Bits), args[1].AsString)));

        registry.Register("std.fmt.formatString", new[] { TypeTag.String, TypeTag.String },
            TypeTag.String,
            args => LyrValue.FromString(Padded(args[0].AsString, args[1].AsString)));

        return registry;
    }

    // ------------------------------------------------------------------ Codepoint-Helfer

    private static long CodepointCount(string s)
    {
        var n = 0L;
        for (var i = 0; i < s.Length; i += char.IsHighSurrogate(s[i]) ? 2 : 1) n++;
        return n;
    }

    /// <summary>Der Codepoint an <paramref name="index"/> — in Codepoints gezaehlt. Ausserhalb
    /// ist ein <c>panic</c> wie bei einem Array-Index (Sprache.md 9): ein Programmierfehler,
    /// kein fangbarer Zustand.</summary>
    private static int CodepointAt(string s, long index)
    {
        if (index < 0) throw OutOfRange(index);

        var seen = 0L;
        for (var i = 0; i < s.Length; i += char.IsHighSurrogate(s[i]) ? 2 : 1)
        {
            if (seen == index) return char.ConvertToUtf32(s, i);
            seen++;
        }
        throw OutOfRange(index);
    }

    private static LyricPanic OutOfRange(long index) =>
        new(VmDiagnostics.IndexOutOfRange, $"string index {index} is out of range");

    private static string Substring(string s, long start, long count)
    {
        if (start < 0 || count < 0) throw OutOfRange(start < 0 ? start : count);

        var offsets = Offsets(s);
        if (start > offsets.Count - 1) throw OutOfRange(start);

        var from = offsets[(int)start];
        var end = start + count;
        var to = end >= offsets.Count - 1 ? s.Length : offsets[(int)end];
        return s[from..to];
    }

    /// <summary>Die UTF-16-Offsets aller Codepoints, plus einen Wachposten am Ende. Damit werden
    /// Codepoint-Positionen zu Slices, ohne dass jede Operation von vorn zaehlt.</summary>
    private static List<int> Offsets(string s)
    {
        var offsets = new List<int>();
        for (var i = 0; i < s.Length; i += char.IsHighSurrogate(s[i]) ? 2 : 1) offsets.Add(i);
        offsets.Add(s.Length);
        return offsets;
    }

    /// <summary>Position in CODEPOINTS, nicht in UTF-16-Einheiten — sonst waere der Rueckgabewert
    /// nicht als Argument fuer charAt oder substring brauchbar. Minus eins, wenn nicht
    /// gefunden.</summary>
    private static long IndexOf(string s, string needle)
    {
        var at = s.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return -1;

        var count = 0L;
        for (var i = 0; i < at; i += char.IsHighSurrogate(s[i]) ? 2 : 1) count++;
        return count;
    }

    private static LyrValue Split(string s, string separator)
    {
        // Ein leerer Trenner hat keine sinnvolle Antwort — .NET liefert den ganzen String
        // zurueck, Python wirft. Hier ist es ein panic: ein Programmierfehler, keine Eingabe.
        if (separator.Length == 0)
            throw new LyricPanic(VmDiagnostics.IndexOutOfRange, "split needs a non-empty separator");

        var parts = s.Split(separator, StringSplitOptions.None);
        var values = new LyrValue[parts.Length];
        for (var i = 0; i < parts.Length; i++) values[i] = LyrValue.FromString(parts[i]);
        return LyrValue.FromObject(values);
    }

    // ------------------------------------------------------------------ std.fmt-Helfer

    /// <summary>Eine Zahl nach einer .NET-Standard-Spec, invariant.
    ///
    /// <para>Eine unbekannte Spec ist ein <c>panic</c> und kein Fehlerwert: sie steht als Literal
    /// im Quelltext und haengt nicht von der Eingabe ab. Ein Programm mit <c>{x:Q9}</c> ist
    /// falsch geschrieben, nicht unglücklich gelaufen — und ein stilles Ausweichen auf die
    /// Standarddarstellung wuerde den Tippfehler bis in die Ausgabe tragen.</para></summary>
    private static LyrValue Formatted(IFormattable value, string spec)
    {
        try
        {
            return LyrValue.FromString(value.ToString(spec, CultureInfo.InvariantCulture));
        }
        catch (FormatException)
        {
            throw new LyricPanic(VmDiagnostics.IndexOutOfRange,
                $"'{spec}' is not a valid format spec");
        }
    }

    /// <summary>Fuer Typen ohne .NET-Standardformate ist die Spec eine Breite: <c>{name:10}</c>
    /// fuellt rechts auf, <c>{name:-10}</c> links. Eine leere Spec laesst den Text, wie er
    /// ist.</summary>
    private static string Padded(string value, string spec)
    {
        if (spec.Length == 0) return value;

        if (!int.TryParse(spec, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                out var width))
            throw new LyricPanic(VmDiagnostics.IndexOutOfRange,
                $"'{spec}' is not a width — for this type a format spec is a number");

        return width < 0 ? value.PadLeft(-width) : value.PadRight(width);
    }

    private static LyrValue ToChars(string s)
    {
        var chars = new List<LyrValue>();
        for (var i = 0; i < s.Length; i += char.IsHighSurrogate(s[i]) ? 2 : 1)
            chars.Add(LyrValue.FromBits((ulong)char.ConvertToUtf32(s, i)));
        return LyrValue.FromObject(chars.ToArray());
    }
}
