using Lyric.Resolver;

namespace Lyric.Sema;

// Semantische Typen (Sprache.md §4), getrennt von den syntaktischen AST-TypeNodes.
// Namen sind bewusst anders (NamedRef/Optional/ArrayOf/…), weil der TypeChecker beide
// Namespaces nutzt. Gleichheit läuft strukturell über LyrType.Equal — NICHT über
// record-==, da Arrays darin nur Referenz-Gleichheit hätten.

public enum PrimitiveKind
{
    Int, Uint, Float,
    Int8, Int16, Int32, Int64,
    Uint8, Uint16, Uint32, Uint64,
    Float32, Float64,
    Bool, Char, String, Void
}

public abstract record LyrType
{
    public static readonly LyrType Error = new ErrorType();
    public static readonly LyrType Null = new NullType();
    public static readonly LyrType Never = new NeverType();
    public static readonly LyrType Bool = new PrimitiveType(PrimitiveKind.Bool);
    public static readonly LyrType Int = new PrimitiveType(PrimitiveKind.Int);
    public static readonly LyrType Float = new PrimitiveType(PrimitiveKind.Float);
    public static readonly LyrType Char = new PrimitiveType(PrimitiveKind.Char);
    public static readonly LyrType String = new PrimitiveType(PrimitiveKind.String);
    public static readonly LyrType Void = new PrimitiveType(PrimitiveKind.Void);

    /// <summary>Strukturelle Typ-Gleichheit.</summary>
    public static bool Equal(LyrType a, LyrType b) => (a, b) switch
    {
        (PrimitiveType x, PrimitiveType y) => x.Kind == y.Kind,
        (NamedRef x, NamedRef y) => ReferenceEquals(x.Symbol, y.Symbol),
        (TypeParamType x, TypeParamType y) => ReferenceEquals(x.Param, y.Param),
        (GenericInstance x, GenericInstance y) => ReferenceEquals(x.Definition, y.Definition) && SameSequence(x.Arguments, y.Arguments),
        (Optional x, Optional y) => Equal(x.Inner, y.Inner),
        (ArrayOf x, ArrayOf y) => x.Size == y.Size && Equal(x.Element, y.Element),
        (TupleOf x, TupleOf y) => SameSequence(x.Elements, y.Elements),
        (FnType x, FnType y) => Equal(x.Return, y.Return) && SameSequence(x.Parameters, y.Parameters),
        (RangeOf x, RangeOf y) => Equal(x.Element, y.Element),
        (ErrorType, ErrorType) => true,
        (NullType, NullType) => true,
        (NeverType, NeverType) => true,
        _ => false
    };

    private static bool SameSequence(LyrType[] a, LyrType[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (!Equal(a[i], b[i])) return false;
        return true;
    }

    public bool IsError => this is ErrorType;
}

public sealed record PrimitiveType(PrimitiveKind Kind) : LyrType;
public sealed record NamedRef(TypeSymbol Symbol) : LyrType;          // struct/class/enum/interface-Instanz (nicht-generisch)
public sealed record TypeParamType(GenericParamSymbol Param) : LyrType; // T innerhalb einer generischen Definition
public sealed record GenericInstance(TypeSymbol Definition, LyrType[] Arguments) : LyrType; // Stack<int>
public sealed record Optional(LyrType Inner) : LyrType;              // ?T
public sealed record ArrayOf(LyrType Element, int? Size) : LyrType;  // T[] / T[N]
public sealed record TupleOf(LyrType[] Elements) : LyrType;
public sealed record FnType(LyrType[] Parameters, LyrType Return) : LyrType;
public sealed record RangeOf(LyrType Element) : LyrType;             // interner Typ von 0..9 (kein Spec-Typ)
public sealed record ErrorType : LyrType;                           // Recovery-Sentinel
public sealed record NullType : LyrType;                            // Typ des null-Literals (nur ?T-zuweisbar)
public sealed record NeverType : LyrType;                           // Rückgabetyp von panic (§9); Bottom-Typ, nicht benennbar
