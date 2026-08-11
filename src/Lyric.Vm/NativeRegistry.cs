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
        TypeTag? ReturnElement = null,
        BytecodeType[]? FullParamTypes = null, BytecodeType? FullReturnType = null);

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

    /// <summary>Ein Native, das ein <c>?T</c> liefert.
    ///
    /// <para>Gebraucht ueberall dort, wo ein Fehlschlag ein <b>gewoehnlicher Zustand der Welt</b>
    /// ist und kein Programmierfehler: eine Datei, die nicht existiert; eine Umgebungsvariable,
    /// die nicht gesetzt ist. Ein <c>panic</c> waere dort falsch, und eine Exception braeuchte
    /// einen Throwable-Typ pro Fall.</para>
    ///
    /// <para>Wie bei Arrays wird der innere Tag mitgeprueft — <c>?string</c> und <c>?int</c>
    /// tragen beide <c>TypeTag.Optional</c>.</para></summary>
    public void RegisterOptionalReturning(string name, TypeTag[] paramTypes, TypeTag inner,
        Func<LyrValue[], LyrValue> implementation) =>
        _natives[name] = new Native(paramTypes, TypeTag.Optional, implementation, inner);

    /// <summary>
    /// Ein Native, dessen Signatur <b>Host-Typen</b> enthaelt (M10/E4, ADR-026).
    ///
    /// <para>Braucht die vollen Typen und nicht nur die Tags: <c>Entity</c> und <c>Sprite</c>
    /// tragen beide <see cref="TypeTag.Host"/> und waeren sonst ununterscheidbar — genau wie
    /// <c>string[]</c> und <c>char[]</c> es ohne den Elementtag waeren. Der Name ist alles, was
    /// Modul und Runtime von einem Host-Typ wissen; er ist damit auch alles, was beim Binden
    /// verglichen werden kann.</para>
    /// </summary>
    public void RegisterWithTypes(string name, BytecodeType[] paramTypes, BytecodeType returnType,
        Func<LyrValue[], LyrValue> implementation) =>
        _natives[name] = new Native(
            paramTypes.Select(p => p.Tag).ToArray(), returnType.Tag, implementation,
            ReturnElement: null, FullParamTypes: paramTypes, FullReturnType: returnType);

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

            // Und bei einem Host-Typ genuegt er erst recht nicht: der NAME unterscheidet ihn,
            // sonst nichts (ADR-026). Ohne diese Pruefung koennte ein Modul, das eine 'Entity'
            // erwartet, eine 'Sprite' bekommen — und der erste Zugriff darauf waere ein
            // InvalidCastException tief im Host, weit weg von der Ursache.
            if (native.FullParamTypes is { } declaredParams)
            {
                for (var p = 0; p < declaredParams.Length; p++)
                    RequireSameHostType(import.Name, declaredParams[p], import.ParamTypes[p],
                        $"parameter {p + 1}");

                RequireSameHostType(import.Name, native.FullReturnType!, import.ReturnType,
                    "the return type");
            }

            bound[i] = new BoundNative(import.ParamTypes.Count,
                import.ReturnType.Tag != TypeTag.Void, native.Implementation);
        }

        return bound;
    }

    private static void RequireSameHostType(string import, BytecodeType native,
        BytecodeType expected, string what)
    {
        if (native.Tag != TypeTag.Host && expected.Tag != TypeTag.Host) return;
        if (string.Equals(native.HostName, expected.HostName, StringComparison.Ordinal)) return;

        throw new LyricRuntimeException(VmDiagnostics.ImportsNotBound,
            $"native '{import}': {what} is host type '{native.HostName ?? "(none)"}', but the "
            + $"module expects '{expected.HostName ?? "(none)"}'");
    }

    /// <summary>
    /// Die eingebauten Natives der Standalone-CLI. <paramref name="output"/> und
    /// <paramref name="error"/> sind Parameter, damit Tests die Ausgabe einsammeln können, ohne
    /// <c>Console</c> umzubiegen.
    /// </summary>
    /// <param name="input">Woher <c>readLine</c> liest. Voreingestellt ist <c>Console.In</c>;
    /// Tests reichen einen <c>StringReader</c> herein, ohne die Konsole umzubiegen. Der Parameter
    /// steht hinten und hat einen Default, damit die bestehenden Aufrufstellen unverändert
    /// bleiben — sie schreiben nur.</param>
    public static NativeRegistry CreateDefault(
        TextWriter output, TextWriter error, TextReader? input = null)
    {
        var registry = new NativeRegistry();
        var str = new[] { TypeTag.String };
        var none = Array.Empty<TypeTag>();
        var stdin = input ?? Console.In;

        registry.Register("std.io.console.print", str, TypeTag.Void,
            args => { output.Write(args[0].AsString); return default; });
        registry.Register("std.io.console.println", str, TypeTag.Void,
            // Immer '\n', nie Environment.NewLine: die Ausgabe eines Lyric-Programms darf nicht
            // vom Betriebssystem abhängen, sonst sind Golden-Tests auf zwei Runnern verschieden.
            args => { output.Write(args[0].AsString); output.Write('\n'); return default; });
        registry.Register("std.io.console.eprintln", str, TypeTag.Void,
            args => { error.Write(args[0].AsString); error.Write('\n'); return default; });

        // 'eprint' fehlte, obwohl 'print' und 'eprintln' da waren — eine Diagnose ohne
        // Zeilenumbruch liess sich nicht schreiben.
        registry.Register("std.io.console.eprint", str, TypeTag.Void,
            args => { error.Write(args[0].AsString); return default; });

        // ---------------------------------------------------------------- Eingabe
        //
        // Bis hierher konnte 'std.io.console' ausschliesslich SCHREIBEN. Ohne 'readLine' gibt es
        // keine interaktiven Programme und keine Filter, die von stdin lesen — die groesste
        // einzelne Luecke der Stdlib.
        //
        // Keine Capability: stdin zu lesen ist wie auf stdout zu schreiben ein gewoehnlicher Teil
        // des Prozesses und keine Zugriffsentscheidung (Doku.md 20.1). Wer es verbieten will, gibt
        // dem Host einen leeren Reader.

        // '?string', weil EOF kein Fehler ist, sondern ein Zustand der Welt: die Eingabe ist zu
        // Ende. Dieselbe Entscheidung wie bei 'readText' und 'env' — erwartbares Scheitern gehoert
        // in den Rueckgabewert, nicht in eine Exception.
        registry.RegisterOptionalReturning("std.io.console.readLine", none, TypeTag.String,
            _ => Optional(stdin.ReadLine()));

        // Alles bis EOF. Fuer Filter, die den ganzen Text brauchen; liefert "" statt null, weil
        // „nichts da" und „leer" hier dasselbe bedeuten.
        registry.Register("std.io.console.readAll", none, TypeTag.String,
            _ => LyrValue.FromString(stdin.ReadToEnd()));

        // Ein einzelner Codepoint. 'Read()' liefert UTF-16-Einheiten, also muss ein Surrogatpaar
        // zusammengesetzt werden — sonst kaeme eine halbe Zeichenhaelfte heraus, und die ist seit
        // ADR-022 nicht einmal ein gueltiger char.
        registry.RegisterOptionalReturning("std.io.console.readChar", none, TypeTag.Char,
            _ => ReadCodepoint(stdin));

        // Terminal oder Pipe? Entscheidet, ob ein Prompt ueberhaupt sinnvoll ist — wer in eine
        // Pipe schreibt, soll keine Eingabeaufforderung in den Datenstrom setzen.
        registry.Register("std.io.console.isInteractive", none, TypeTag.Bool,
            _ => LyrValue.FromBool(!Console.IsInputRedirected && !Console.IsOutputRedirected));

        // Noetig, sobald ein Prompt OHNE Zeilenumbruch geschrieben wird: sonst steht die Frage
        // noch im Puffer, waehrend das Programm schon auf die Antwort wartet.
        registry.Register("std.io.console.flush", none, TypeTag.Void,
            _ => { output.Flush(); return default; });

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
        // --- std.math (ungated: Rechnen fasst nichts an) ---------------------------------
        //
        // Sonderwerte folgen IEEE 754, wie Sprache.md 6.6 es fuer Fliesskomma festlegt:
        // 'sqrt(-1.0)' ist NaN und kein panic. Ein Fehlerfall waere hier eine Erfindung — die
        // Hardware kennt keinen, und ein Programm, das ihn faengt, wuerde auf einer anderen
        // Runtime anders laufen.
        var f1 = new[] { TypeTag.F64 };
        var f2 = new[] { TypeTag.F64, TypeTag.F64 };

        registry.Register("std.math.sqrt", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Sqrt(args[0].AsF64)));
        registry.Register("std.math.abs", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Abs(args[0].AsF64)));
        registry.Register("std.math.floor", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Floor(args[0].AsF64)));
        registry.Register("std.math.ceil", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Ceiling(args[0].AsF64)));

        // "round half to even" — dasselbe, was .NET ohne Angabe tut. Konsequentes Aufrunden
        // traegt ueber viele Werte einen systematischen Fehler ein.
        registry.Register("std.math.round", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Round(args[0].AsF64, MidpointRounding.ToEven)));

        registry.Register("std.math.min", f2, TypeTag.F64,
            args => LyrValue.FromF64(Math.Min(args[0].AsF64, args[1].AsF64)));
        registry.Register("std.math.max", f2, TypeTag.F64,
            args => LyrValue.FromF64(Math.Max(args[0].AsF64, args[1].AsF64)));
        registry.Register("std.math.pow", f2, TypeTag.F64,
            args => LyrValue.FromF64(Math.Pow(args[0].AsF64, args[1].AsF64)));

        registry.Register("std.math.sin", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Sin(args[0].AsF64)));
        registry.Register("std.math.cos", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Cos(args[0].AsF64)));
        registry.Register("std.math.tan", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Tan(args[0].AsF64)));
        // Die Umkehrfunktionen der Trigonometrie — die einzigen neuen Natives in std.math
        // (M8b/S6). Alles andere dort ist aus sqrt/pow/log ableitbar und steht in Lyric.
        // log2 und log10 nativ, obwohl 'log(x)/log(basis)' sie ausdrueckt: die Umrechnung ist
        // UNGENAU. Gemessen lieferte 'log10(1000.0)' dort 2.9999999999999996, und 'as int' machte
        // daraus 2. Eine Bibliothek, bei der 'log10' einer Zehnerpotenz danebenliegt, ist fuer
        // ihren haeufigsten Zweck kaputt — Stellenzahl ausrechnen.
        //
        // Ableitbar heisst nicht gleichwertig. Die Regel "was die Sprache kann, bleibt in Lyric"
        // gilt fuer Ausdrucksstaerke, nicht gegen Genauigkeit.
        registry.Register("std.string.fromUint", new[] { TypeTag.U64 }, TypeTag.String,
            args => LyrValue.FromString(args[0].AsU64.ToString(CultureInfo.InvariantCulture)));

        registry.Register("std.fmt.formatUint", new[] { TypeTag.U64, TypeTag.String },
            TypeTag.String, args => Formatted(args[0].AsU64, args[1].AsString));

        registry.Register("std.math.log2", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Log2(args[0].AsF64)));
        registry.Register("std.math.log10", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Log10(args[0].AsF64)));

        registry.Register("std.math.asin", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Asin(args[0].AsF64)));
        registry.Register("std.math.acos", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Acos(args[0].AsF64)));
        registry.Register("std.math.atan", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Atan(args[0].AsF64)));
        registry.Register("std.math.atan2", new[] { TypeTag.F64, TypeTag.F64 }, TypeTag.F64,
            args => LyrValue.FromF64(Math.Atan2(args[0].AsF64, args[1].AsF64)));

        registry.Register("std.math.log", f1, TypeTag.F64,
            args => LyrValue.FromF64(Math.Log(args[0].AsF64)));

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

        registry.RegisterOptionalReturning("std.os.env", str, TypeTag.String,
            args => Optional(Environment.GetEnvironmentVariable(args[0].AsString)));

        registry.Register("std.os.currentDir", Array.Empty<TypeTag>(), TypeTag.String,
            _ => LyrValue.FromString(Directory.GetCurrentDirectory()));

        // Beendet sofort. Kein 'defer' laeuft mehr, kein 'catch' greift — der Rueckgabetyp ist
        // trotzdem 'void' und nicht 'never': 'never' hiesse, der Compiler duerfe den folgenden
        // Code als unerreichbar behandeln, und das kann er einem Native nicht ansehen.
        registry.Register("std.os.exit", new[] { TypeTag.I64 }, TypeTag.Void,
            args => { Environment.Exit((int)(args[0].AsI64 & 0xFF)); return default; });

        // --- std.io.file (permission-gated: fileAccess) ------------------------------------
        //
        // Fehler sind RUECKGABEWERTE, keine Exceptions. Eine Datei, die nicht existiert, ist ein
        // gewoehnlicher Zustand der Welt und kein Programmierfehler; ein 'panic' waere dort
        // falsch, und eine Exception braeuchte einen Throwable-Typ pro Fall. Dieselbe
        // Entscheidung wie bei 'List.pop' und 'std.os.env'.
        //
        // Gefangen wird bewusst breit (IOException, Zugriffsrechte, ungueltige Pfade): fuer den
        // Aufrufer sind das alles dieselbe Antwort — "ging nicht". Wer den Grund braucht, kann
        // ihn in v1 nicht bekommen; das waere ein Fehler-Enum und damit eine Sprachentscheidung.
        registry.RegisterOptionalReturning("std.io.file.readText", str, TypeTag.String,
            args => Optional(TryIo(() => File.ReadAllText(args[0].AsString))));

        registry.Register("std.io.file.writeText", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool, args => LyrValue.FromBool(
                TryIo(() => { File.WriteAllText(args[0].AsString, args[1].AsString); return ""; }) is not null));

        registry.Register("std.io.file.appendText", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool, args => LyrValue.FromBool(
                TryIo(() => { File.AppendAllText(args[0].AsString, args[1].AsString); return ""; }) is not null));

        registry.Register("std.io.file.exists", str, TypeTag.Bool,
            args => LyrValue.FromBool(File.Exists(args[0].AsString)));

        // 'true', wenn die Datei danach weg ist — auch wenn sie vorher schon nicht existierte.
        // Die Frage des Aufrufers ist "ist sie weg", nicht "habe ich sie geloescht".
        registry.Register("std.io.file.remove", str, TypeTag.Bool,
            args => LyrValue.FromBool(
                TryIo(() => { File.Delete(args[0].AsString); return ""; }) is not null));

        registry.RegisterArrayReturning("std.io.file.readLines", str, TypeTag.String,
            args => Lines(TryIo(() => File.ReadAllText(args[0].AsString))));

        // ------------------------------------------------- std.io.file, Erweiterung (M8b/S8)
        //
        // Alles hier laeuft durch 'TryIo': eine fehlende Datei, ein gesperrtes Verzeichnis oder
        // ein voller Datentraeger sind gewoehnliche Zustaende der Welt. Eine .NET-Exception
        // mitten in einem Lyric-Programm waere die falsche Antwort — der Rueckgabewert sagt es.

        // Nicht ueber 'TryIo': das reicht einen STRING durch, und der Umweg ueber
        // 'Length.ToString()' und 'long.Parse' war nicht nur haesslich, sondern falsch — der
        // erste Versuch lieferte fuer eine existierende Datei 'null'.
        registry.RegisterOptionalReturning("std.io.file.size", str, TypeTag.I64,
            args =>
            {
                try
                {
                    var info = new FileInfo(args[0].AsString);

                    // 'Some' und nicht 'FromI64': bei einem '?T' ueber einem SKALAR markiert
                    // erst der Marker in 'Ref' die Anwesenheit — jedes Bitmuster ist eine
                    // gueltige Zahl, es gibt also keins fuer "kein Wert" (Bytecode.md §5).
                    //
                    // Die bisherigen optionalen Natives liefern alle 'string' und trugen ihre
                    // Referenz selbst; dieses hier ist das erste mit einem Skalar, und deshalb
                    // das erste, das ohne 'Some' still 'null' zurueckgab.
                    return info.Exists ? LyrValue.Some(LyrValue.FromI64(info.Length)) : LyrValue.None;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
                {
                    return LyrValue.None;
                }
            });

        registry.Register("std.io.file.isFile", str, TypeTag.Bool,
            args => LyrValue.FromBool(File.Exists(args[0].AsString)));

        registry.Register("std.io.file.isDirectory", str, TypeTag.Bool,
            args => LyrValue.FromBool(Directory.Exists(args[0].AsString)));

        registry.Register("std.io.file.copy", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool, args => LyrValue.FromBool(
                TryIo(() => { File.Copy(args[0].AsString, args[1].AsString, true); return ""; })
                is not null));

        registry.Register("std.io.file.move", new[] { TypeTag.String, TypeTag.String },
            TypeTag.Bool, args => LyrValue.FromBool(
                TryIo(() => { File.Move(args[0].AsString, args[1].AsString, true); return ""; })
                is not null));

        // Ein bereits vorhandenes Verzeichnis ist Erfolg: der gewuenschte Zustand ist erreicht.
        // '.NET' verhaelt sich bei CreateDirectory ohnehin so; das hier haelt es fest.
        registry.Register("std.io.file.createDir", str, TypeTag.Bool,
            args => LyrValue.FromBool(
                TryIo(() => { Directory.CreateDirectory(args[0].AsString); return ""; })
                is not null));

        registry.Register("std.io.file.createDirAll", str, TypeTag.Bool,
            args => LyrValue.FromBool(
                TryIo(() => { Directory.CreateDirectory(args[0].AsString); return ""; })
                is not null));

        // Nur LEER — 'recursive: false'. Ein rekursives Loeschen gibt es in der Stdlib bewusst
        // nicht; wer es braucht, schreibt die Schleife sichtbar hin.
        registry.Register("std.io.file.removeDir", str, TypeTag.Bool,
            args => LyrValue.FromBool(
                TryIo(() => { Directory.Delete(args[0].AsString, false); return ""; })
                is not null));

        registry.RegisterArrayReturning("std.io.file.listDir", str, TypeTag.String,
            args =>
            {
                try
                {
                    var namen = Directory.EnumerateFileSystemEntries(args[0].AsString)
                        .Select(Path.GetFileName)
                        .OfType<string>()
                        .OrderBy(n => n, StringComparer.Ordinal)   // deterministisch, ADR-013
                        .Select(LyrValue.FromString)
                        .ToArray();
                    return LyrValue.FromObject(namen);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                              or ArgumentException)
                {
                    return LyrValue.FromObject([]);
                }
            });

        registry.Register("std.io.file.tempDir", none, TypeTag.String,
            _ => LyrValue.FromString(Path.GetTempPath()));

        // ------------------------------------------------------ std.os, Erweiterung (M8b/S8)

        registry.RegisterArrayReturning("std.os.args", none, TypeTag.String,
            _ => LyrValue.FromObject(Environment.GetCommandLineArgs()
                .Select(LyrValue.FromString).ToArray()));

        registry.Register("std.os.setEnv", new[] { TypeTag.String, TypeTag.String }, TypeTag.Bool,
            args =>
            {
                try
                {
                    Environment.SetEnvironmentVariable(args[0].AsString, args[1].AsString);
                    return LyrValue.FromBool(true);
                }
                catch (Exception e) when (e is ArgumentException or System.Security.SecurityException)
                {
                    return LyrValue.FromBool(false);
                }
            });

        registry.RegisterOptionalReturning("std.os.hostName", none, TypeTag.String,
            _ => Optional(TryIo(() => Environment.MachineName)));

        registry.RegisterOptionalReturning("std.os.userName", none, TypeTag.String,
            _ => Optional(TryIo(() => Environment.UserName)));

        registry.RegisterOptionalReturning("std.os.homeDir", none, TypeTag.String,
            _ => Optional(TryIo(() =>
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));

        registry.Register("std.os.cpuCount", none, TypeTag.I64,
            _ => LyrValue.FromI64(Environment.ProcessorCount));

        registry.Register("std.os.nowMillis", none, TypeTag.I64,
            _ => LyrValue.FromI64(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        // Monoton und mit beliebigem Nullpunkt — deshalb NICHT mit nowMillis vergleichbar. Genau
        // das ist der Zweck: eine Systemuhr kann waehrend einer Messung springen.
        registry.Register("std.os.nowNanos", none, TypeTag.I64,
            _ => LyrValue.FromI64(
                (long)(System.Diagnostics.Stopwatch.GetTimestamp()
                       * (1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency))));

        registry.Register("std.os.sleep", new[] { TypeTag.I64 }, TypeTag.Void,
            args =>
            {
                var millis = args[0].AsI64;
                if (millis > 0) Thread.Sleep((int)Math.Min(millis, int.MaxValue));
                return default;
            });


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

    // ------------------------------------------------------------------ std.os/std.io.file

    /// <summary>Ein <c>?string</c> aus einem moeglicherweise fehlenden Wert. Die Darstellung
    /// folgt P2b: eine Referenz heisst „hat einen Wert", eine leere heisst <c>null</c>.</summary>
    private static LyrValue Optional(string? value) =>
        value is null ? default : LyrValue.FromString(value);

    /// <summary>
    /// Ein Codepoint von einem <see cref="TextReader"/> — Surrogatpaare zusammengesetzt.
    /// </summary>
    /// <remarks>.NETs <c>Read()</c> liefert UTF-16-Einheiten, Lyrics <c>char</c> ist ein
    /// Codepoint (Sprache.md §4). Ohne das Zusammensetzen käme für ein Zeichen jenseits der BMP
    /// eine einzelne Surrogathälfte zurück — und die ist seit ADR-022 kein gültiger <c>char</c>,
    /// würde also beim Erzeugen panicen. Der Fehler wäre erst beim Drucken sichtbar geworden.
    /// </remarks>
    private static LyrValue ReadCodepoint(TextReader reader)
    {
        var first = reader.Read();
        if (first < 0) return default;   // EOF

        if (char.IsHighSurrogate((char)first) && reader.Peek() is var next && next >= 0
            && char.IsLowSurrogate((char)next))
            return LyrValue.FromBits((ulong)char.ConvertToUtf32((char)first, (char)reader.Read()));

        return LyrValue.FromBits((ulong)first);
    }

    /// <summary>Fuehrt eine Dateioperation aus und liefert <c>null</c>, wenn sie fehlschlaegt.
    ///
    /// <para>Gefangen wird breit — fehlende Datei, fehlende Rechte, ungueltiger Pfad, Geraet weg.
    /// Fuer den Aufrufer sind das alles dieselbe Antwort. Wer den Grund braucht, kann ihn in v1
    /// nicht bekommen: das waere ein Fehler-Enum in der Stdlib und damit eine
    /// Sprachentscheidung.</para></summary>
    private static string? TryIo(Func<string> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Zeilen ohne Zeilenenden. Ob die Datei mit CRLF oder LF geschrieben war, ist
    /// danach nicht mehr sichtbar — sonst haenge das Ergebnis eines Programms daran, auf welchem
    /// System seine Eingabe entstanden ist.</summary>
    private static LyrValue Lines(string? content)
    {
        if (content is null) return LyrValue.FromObject([]);

        var split = content.ReplaceLineEndings("\n").Split('\n');

        // Eine Datei, die mit einem Zeilenumbruch endet, hat danach keine leere letzte Zeile.
        var count = split.Length > 0 && split[^1].Length == 0 ? split.Length - 1 : split.Length;

        var values = new LyrValue[count];
        for (var i = 0; i < count; i++) values[i] = LyrValue.FromString(split[i]);
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
        // Eine reine Zahl ist eine BREITE, auch bei einem Zahlenwert: '{n:8}' rechtsbuendig auf
        // acht Stellen, '{n:-8}' linksbuendig. Ohne diesen Fall reicht sie .NET durch, und dort
        // ist '8' ein Custom-Format (Ziffern-Platzhalter) und '-8' sogar ein Literal — aus
        // '{c.lines:-8}' wurde woertlich "-8". Gefunden vom M8-Gate.
        //
        // Damit gilt die Breiten-Form fuer ALLE Typen und nicht nur fuer die, denen .NET keine
        // Standardformate gibt. Eine Regel, die je nach Typ etwas anderes bedeutet, waere die
        // schlechtere Antwort.
        if (IsWidth(spec)) return LyrValue.FromString(Padded(
            value.ToString(null, CultureInfo.InvariantCulture), spec));

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
    /// <summary>Ist die Spec eine reine Breite — Ziffern, optional mit fuehrendem Minus?</summary>
    private static bool IsWidth(string spec)
    {
        if (spec.Length == 0) return false;

        var start = spec[0] == '-' ? 1 : 0;
        if (start >= spec.Length) return false;

        for (var i = start; i < spec.Length; i++)
            if (!char.IsAsciiDigit(spec[i]))
                return false;
        return true;
    }

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
