using Lyric.AST;
using Lyric.Resolver;

namespace Lyric.Sema;

/// <summary>
/// What may stand in an attribute argument, resolved down to the literal that goes into the
/// metadata row.
///
/// <para>A row holds a VALUE, so whatever is written has to be one at compile time — that much is
/// forced by the format and does not bend. What bends is where the value may be spelled: a literal
/// at the use site, or a name bound to one. Without the second, a program that publishes its
/// vocabulary — event names, kinds, versions — has to repeat the raw string at every use, and a
/// typo there is a receiver that stays silent rather than a compile error. That is the fault class
/// attributes were adopted to remove on the sending side.</para>
///
/// <para>NOT constant folding, and deliberately not: Lyric has no such pass anywhere, and a
/// <c>let</c> is an ordinary global slot filled at load time. What this reads is the one shape
/// whose value is already written in the source — a binding whose initializer IS a literal,
/// possibly through a chain of them. Everything else stays a runtime expression, including
/// <c>1 + 2</c>, which the compiler could compute but has nowhere to put.</para>
///
/// <para>One place, because the sema decides whether a use is legal and the lowering writes what
/// it decided. Two walks would eventually answer differently, and the second one would answer
/// inside a compiler that has already promised the first.</para>
/// </summary>
public static class AttributeValues
{
    /// <summary>The literal an attribute argument denotes, or <c>null</c> when it denotes
    /// none.</summary>
    public static Expr? LiteralOf(Expr expr, TypeResult types) =>
        Resolve(expr, types, new HashSet<Symbol>(ReferenceEqualityComparer.Instance));

    private static Expr? Resolve(Expr expr, TypeResult types, HashSet<Symbol> seen) => expr switch
    {
        // 'null' is excluded on purpose — an attribute field is a scalar, a char or a string,
        // never an optional.
        IntLiteralExpr or FloatLiteralExpr or StringLiteralExpr or CharLiteralExpr
            or BoolLiteralExpr => expr,
        UnaryExpr { Operator: UnaryOp.Neg, Operand: IntLiteralExpr or FloatLiteralExpr } => expr,
        IdentifierExpr or MemberExpr => ThroughBinding(expr, types, seen),
        _ => null,
    };

    /// <summary>
    /// A name: selective (<c>import api { CLEARED };</c>), module-qualified (<c>api.CLEARED</c>)
    /// or a <c>static let</c> on a type — all three arrive here as a symbol the checker already
    /// bound, so nothing is resolved a second time.
    /// </summary>
    private static Expr? ThroughBinding(Expr expr, TypeResult types, HashSet<Symbol> seen)
    {
        var symbol = types.RefOf(expr);
        while (symbol is ImportBindingSymbol imported) symbol = imported.Target;

        // A GlobalSymbol and nothing else: a local, a parameter or a field is bound per run, and
        // the module the row lands in has no place to keep one.
        if (symbol is not GlobalSymbol global) return null;

        // A binding may name another; the set breaks a cycle rather than trusting that the
        // declaration-order rule (LYR-SEM0057) has already refused one.
        if (!seen.Add(global)) return null;

        var binding = global.Declaration switch
        {
            GlobalBindingDecl g => g.Binding,
            StaticBindingDecl s => s.Binding,
            _ => null,
        };

        return binding is { IsMutable: false, Initializer: { } initializer }
            ? Resolve(initializer, types, seen)
            : null;
    }
}
