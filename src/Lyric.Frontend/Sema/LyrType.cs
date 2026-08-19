using Lyric.Resolver;

namespace Lyric.Sema;

// Semantic types, separate from the syntactic AST TypeNodes. The names differ deliberately
// (NamedRef, Optional, ArrayOf, …), because the TypeChecker uses both namespaces. Equality is
// structural through LyrType.Equal, NOT through record ==, under which arrays would compare by
// reference.

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

    /// <summary>Structural type equality.</summary>
    public static bool Equal(LyrType a, LyrType b) => (a, b) switch
    {
        (PrimitiveType x, PrimitiveType y) => x.Kind == y.Kind,
        (NamedRef x, NamedRef y) => ReferenceEquals(x.Symbol, y.Symbol),
        // By SYMBOL, deliberately not by underlying: two opaque aliases of int are two types,
        // and an opaque alias never equals its underlying — that is the point of it.
        (OpaqueRef x, OpaqueRef y) => ReferenceEquals(x.Symbol, y.Symbol),
        (TypeParamType x, TypeParamType y) => ReferenceEquals(x.Param, y.Param),
        (GenericInstance x, GenericInstance y) => ReferenceEquals(x.Definition, y.Definition) && SameSequence(x.Arguments, y.Arguments),
        (Optional x, Optional y) => Equal(x.Inner, y.Inner),
        (ArrayOf x, ArrayOf y) => x.Size == y.Size && Equal(x.Element, y.Element),
        (TupleOf x, TupleOf y) => SameSequence(x.Elements, y.Elements),
        (FnType x, FnType y) => Equal(x.Return, y.Return) && SameSequence(x.Parameters, y.Parameters),
        (RangeOf x, RangeOf y) => Equal(x.Element, y.Element),
        (CoroutineOf x, CoroutineOf y) => Equal(x.Yield, y.Yield),
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
public sealed record NamedRef(TypeSymbol Symbol) : LyrType;          // a struct, class, enum or interface instance, non-generic

/// <summary>An <c>opaque type</c> alias (v1.15): its IDENTITY is the symbol, its layout the
/// underlying type. The sema compares by symbol — nothing converts implicitly, only an explicit
/// <c>as</c> crosses — while the lowering sees only <see cref="Underlying"/>: at runtime an
/// opaque value IS its underlying value, which is what lets it cross the native boundary.</summary>
public sealed record OpaqueRef(TypeSymbol Symbol, LyrType Underlying) : LyrType;
public sealed record TypeParamType(GenericParamSymbol Param) : LyrType; // T inside a generic definition
public sealed record GenericInstance(TypeSymbol Definition, LyrType[] Arguments) : LyrType; // Stack<int>
public sealed record Optional(LyrType Inner) : LyrType;              // ?T
public sealed record ArrayOf(LyrType Element, int? Size) : LyrType;  // T[] and T[N]
public sealed record TupleOf(LyrType[] Elements) : LyrType;
public sealed record FnType(LyrType[] Parameters, LyrType Return) : LyrType;
public sealed record RangeOf(LyrType Element) : LyrType;             // the internal type of 0..9, not a spec type
public sealed record CoroutineOf(LyrType Yield) : LyrType;           // Coroutine<T>, an internal type like RangeOf
/// <summary>
/// The recovery sentinel. It means "a diagnostic has already been reported here" — not "unknown",
/// not "not computed yet", not "do not care".
///
/// <para>The invariant behind it: whoever sees an <c>ErrorType</c> stays silent, so one error does
/// not turn into an avalanche of follow-ups. Whoever PRODUCES one must therefore have reported
/// first.</para>
///
/// <para><c>Lyric.Tests.Sema.ErrorTypeInvariantTests</c> checks it mechanically: if an
/// <c>ErrorType</c> appears anywhere in a program, a diagnostic has to be present.</para>
/// </summary>
public sealed record ErrorType : LyrType;

/// <summary>
/// The expression NAMES something — a type, a module — that is not a value.
///
/// <para>Distinct from <see cref="ErrorType"/>: <c>Error</c> means "a diagnostic has already been
/// reported here", and every consumer stays silent on it. Mixing the two turns "I do not know what
/// this is" into a silent pass, under which <c>P(1,2,3).nonsense</c> would check out completely
/// without anything being reported.</para>
///
/// <para>Legal at exactly one place: as the TARGET OF A MEMBER ACCESS (<c>Point.new(…)</c>,
/// <c>console.println(…)</c>). Everywhere else <c>TypeChecker.CheckExpr</c> reports
/// <c>LYR-SEM0052</c> and degrades to <see cref="ErrorType"/>, from where the ordinary poison rule
/// applies again.</para>
/// </summary>
/// <param name="Instance">The resolved instance in <c>Pair&lt;int&gt;.of(3)</c>. Without it the
/// member yields its type with <c>T</c> still in it, and the error arrives as "cannot assign 'int'
/// to 'T'" one level too late. <c>null</c> for every non-generic type and every module.</param>
public sealed record NonValueType(Symbol Symbol, string Kind, GenericInstance? Instance = null) : LyrType;
public sealed record NullType : LyrType;                            // the type of the null literal, assignable only to ?T
public sealed record NeverType : LyrType;                           // the return type of panic; a bottom type, not nameable
