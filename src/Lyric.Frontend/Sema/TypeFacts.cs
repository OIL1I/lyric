using Lyric.Resolver;
using System.Text;

namespace Lyric.Sema;

/// <summary>Klassifikation, Anzeige und Konvertierbarkeit von Typen (Sprache.md §4).</summary>
public static class TypeFacts
{
    /// <summary>
    /// Ganzzahl im Sinne von <c>Sprache.md</c> §6.5 — <b>einschliesslich <c>char</c></b> (ADR-022).
    ///
    /// <para>Ein <c>char</c> ist ein Unicode-Codepoint und damit eine Zahl; er zaehlt zur Numerik,
    /// weil <c>std.string</c> sonst fuer „ist das eine Ziffer?" in den Host absteigen muesste.
    /// Der Preis steht in der VM: jede Operation, die einen <c>char</c> ERZEUGT, prueft den
    /// Wertebereich, damit die Zusage aus §4 wahr bleibt.</para>
    ///
    /// <para>Diese Frage wird ein zweites Mal in <c>IrVerifier.IsInteger</c> beantwortet — auf
    /// <c>IrType</c> statt <c>LyrType</c>, weil der Verifier den Bytecode ohne die Sema pruefen
    /// koennen muss (ADR-013). <b>Wer hier einen Typ ergaenzt, ergaenzt ihn auch dort</b>, sonst
    /// lehnt der Verifier ab, was die Sema erlaubt.</para>
    /// </summary>
    public static bool IsInteger(LyrType t) => t is PrimitiveType p && p.Kind is
        PrimitiveKind.Int or PrimitiveKind.Uint
        or PrimitiveKind.Int8 or PrimitiveKind.Int16 or PrimitiveKind.Int32 or PrimitiveKind.Int64
        or PrimitiveKind.Uint8 or PrimitiveKind.Uint16 or PrimitiveKind.Uint32 or PrimitiveKind.Uint64
        or PrimitiveKind.Char;

    public static bool IsFloat(LyrType t) => t is PrimitiveType p && p.Kind is
        PrimitiveKind.Float or PrimitiveKind.Float32 or PrimitiveKind.Float64;

    public static bool IsNumeric(LyrType t) => IsInteger(t) || IsFloat(t);
    public static bool IsBool(LyrType t) => t is PrimitiveType { Kind: PrimitiveKind.Bool };
    public static bool IsString(LyrType t) => t is PrimitiveType { Kind: PrimitiveKind.String };
    public static bool IsVoid(LyrType t) => t is PrimitiveType { Kind: PrimitiveKind.Void };

    private static readonly Dictionary<string, PrimitiveKind> Builtins = new()
    {
        ["int"] = PrimitiveKind.Int, ["uint"] = PrimitiveKind.Uint, ["float"] = PrimitiveKind.Float,
        ["int8"] = PrimitiveKind.Int8, ["int16"] = PrimitiveKind.Int16, ["int32"] = PrimitiveKind.Int32, ["int64"] = PrimitiveKind.Int64,
        ["uint8"] = PrimitiveKind.Uint8, ["uint16"] = PrimitiveKind.Uint16, ["uint32"] = PrimitiveKind.Uint32, ["uint64"] = PrimitiveKind.Uint64,
        ["float32"] = PrimitiveKind.Float32, ["float64"] = PrimitiveKind.Float64,
        ["bool"] = PrimitiveKind.Bool, ["char"] = PrimitiveKind.Char, ["string"] = PrimitiveKind.String, ["void"] = PrimitiveKind.Void
    };

    public static LyrType? FromBuiltinName(string name) =>
        Builtins.TryGetValue(name, out var kind) ? new PrimitiveType(kind) : null;

    /// <summary>Passt ein (ggf. negiertes) Ganzzahl-Literal in den Zieltyp? (Entscheidung ②a)</summary>
    public static bool IntLiteralFits(bool negative, ulong magnitude, PrimitiveKind target) => target switch
    {
        PrimitiveKind.Int8 => negative ? magnitude <= 128 : magnitude <= 127,
        PrimitiveKind.Int16 => negative ? magnitude <= 32768 : magnitude <= 32767,
        PrimitiveKind.Int32 => negative ? magnitude <= 2147483648 : magnitude <= 2147483647,
        PrimitiveKind.Int64 or PrimitiveKind.Int => negative ? magnitude <= 9223372036854775808 : magnitude <= 9223372036854775807,
        PrimitiveKind.Uint8 => !negative && magnitude <= 255,
        PrimitiveKind.Uint16 => !negative && magnitude <= 65535,
        PrimitiveKind.Uint32 => !negative && magnitude <= 4294967295,
        PrimitiveKind.Uint64 or PrimitiveKind.Uint => !negative,

        // 'c + 1' und 'let c: char = 65' (ADR-022). Die Grenze ist nicht die eines Ganzzahltyps,
        // sondern die von Unicode — und sie steht in Lyric.Core, weil die VM dieselbe Regel auf
        // gerechnete Ergebnisse anwendet. Was hier durchgeht, darf die VM erzeugen.
        //
        // Ein Literal ist damit zur UEBERSETZUNGSZEIT abgelehnt, wo die Laufzeit sonst panicen
        // muesste: 'let c: char = 0xD800' ist ein Typfehler, kein Absturz.
        PrimitiveKind.Char => !negative && magnitude <= long.MaxValue
                              && Core.Unicode.IsCodepoint((long)magnitude),

        _ => false
    };

    /// <summary>
    /// Das <see cref="TypeSymbol"/> hinter einem benannten Typ — <c>null</c>, wenn keines
    /// dahintersteckt (Skalar, Array, Funktionstyp …).
    ///
    /// <para><b>Warum das eine Funktion sein muss.</b> Ein benannter Typ tritt in zwei Formen auf:
    /// <see cref="NamedRef"/> fuer <c>Box</c> und <see cref="GenericInstance"/> fuer
    /// <c>Box&lt;int&gt;</c>. Fast jede Frage, die man ihm stellt — welche Art, welche Konformanz,
    /// welches Feld — hat fuer beide dieselbe Antwort, und wer sie einzeln behandelt, vergisst
    /// irgendwann die zweite.</para>
    ///
    /// <para>Genau das ist in M7/P8 <b>fuenfmal</b> passiert: bei der Konformanz, beim virtuellen
    /// Aufruf, beim Slot-Index, in der Impl-Tabelle und bei der Feld-Mutabilitaet. Der letzte Fall
    /// machte ein Feld einer generischen Klasse <b>nie</b> schreibbar und damit jeden Iterator
    /// unmoeglich. Jedes Mal war die Ursache dieselbe: ein Muster, das nur <c>NamedRef</c>
    /// nannte.</para>
    /// </summary>
    public static TypeSymbol? SymbolOf(LyrType type) => type switch
    {
        NamedRef named => named.Symbol,
        GenericInstance instance => instance.Definition,
        _ => null,
    };

    /// <summary>Die Art eines benannten Typs — Klasse, Struct, Enum, Interface. <c>null</c>, wenn
    /// es kein benannter Typ ist.</summary>
    public static TypeSymbolKind? KindOf(LyrType type) => SymbolOf(type)?.Kind;

    /// <summary>Ist das ein benannter Typ dieser Art? Der Fall, den die meisten Aufrufer
    /// brauchen — inklusive Instanzen.</summary>
    public static bool Is(LyrType type, TypeSymbolKind kind) => KindOf(type) == kind;

    /// <summary>Ist das ein benannter Typ <b>einer</b> dieser Arten?</summary>
    public static bool IsAny(LyrType type, params TypeSymbolKind[] kinds) =>
        KindOf(type) is { } actual && Array.IndexOf(kinds, actual) >= 0;

    public static string Display(LyrType t)
    {
        switch (t)
        {
            case PrimitiveType p: return p.Kind switch
            {
                PrimitiveKind.Int => "int", PrimitiveKind.Uint => "uint", PrimitiveKind.Float => "float",
                PrimitiveKind.Int8 => "int8", PrimitiveKind.Int16 => "int16", PrimitiveKind.Int32 => "int32", PrimitiveKind.Int64 => "int64",
                PrimitiveKind.Uint8 => "uint8", PrimitiveKind.Uint16 => "uint16", PrimitiveKind.Uint32 => "uint32", PrimitiveKind.Uint64 => "uint64",
                PrimitiveKind.Float32 => "float32", PrimitiveKind.Float64 => "float64",
                PrimitiveKind.Bool => "bool", PrimitiveKind.Char => "char", PrimitiveKind.String => "string", PrimitiveKind.Void => "void",
                _ => "?"
            };
            case NamedRef n: return n.Symbol.Name;
            case TypeParamType tp: return tp.Param.Name;
            case GenericInstance gi: return gi.Definition.Name + "<" + string.Join(", ", gi.Arguments.Select(Display)) + ">";
            case Optional o: return "?" + Display(o.Inner);
            // Ein Funktionstyp als Elementtyp MUSS geklammert werden: 'fn(int) -> void[]' liest
            // sich sonst als Funktion, die 'void[]' liefert. Ohne die Klammer meldete die Sema
            // „cannot assign 'fn(int) -> void[]' to '(fn(int) -> void)[]'" — zwei Anzeigen fuer
            // Typen, die verschieden SIND, aber gleich aussahen, und der Leser sucht den Fehler
            // an der falschen Stelle.
            case ArrayOf { Element: FnType } fnArray:
                return $"({Display(fnArray.Element)})" + (fnArray.Size is null ? "[]" : $"[{fnArray.Size}]");
            case ArrayOf a: return Display(a.Element) + (a.Size is null ? "[]" : $"[{a.Size}]");
            case TupleOf tu: return "(" + string.Join(", ", tu.Elements.Select(Display)) + ")";
            case FnType f: return "fn(" + string.Join(", ", f.Parameters.Select(Display)) + ") -> " + Display(f.Return);
            case RangeOf r: return "range<" + Display(r.Element) + ">";
            case CoroutineOf co: return "Coroutine<" + Display(co.Yield) + ">";
            case NullType: return "null";
            case NeverType: return "never";
            case ErrorType: return "<error>";
            default: return "<?>";
        }
    }
}
