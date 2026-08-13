using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Builds the synthetic function that fills all global slots.
///
/// <para>It looks like any other function — no parameters, return type <c>void</c>, one block, one
/// <c>ret</c> — and is verified and executed as one. Its special role stands solely in the module
/// (<see cref="IrModule.GlobalInit"/>) and in the promise that a runtime calls it BEFORE the entry
/// point.</para>
///
/// <para>A FUNCTION RATHER THAN VALUES IN THE SECTION. A <c>static let ZERO: Vector3 =
/// Vector3 { … }</c> is an expression, not a literal — as a value in the bytecode it would be
/// representable for scalars only, and the rest would need code anyway. A function can do everything
/// the lowering can do, and the instruction set gets no special case. CIL solves it the same way with
/// <c>.cctor</c>.</para>
///
/// <para>THE ORDER IS DECLARATION ORDER. A global may read an earlier declared one, not a later one;
/// that follows from filling the slots in sequence here, and it is the only order without a dependency
/// analysis. Reading a later one yields the zero value.</para>
/// </summary>
internal static class GlobalInitializer
{
    /// <summary>The name appears in the bytecode and must therefore be unable to collide with any Lyric
    /// identifier: <c>&lt;</c> is not allowed in an identifier.</summary>
    public const string Name = "<globals>";

    public static IrFunction Build(GlobalTable globals, TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable, LambdaTable lambdas, InstanceTable instances)
    {
        // A synthetic FunctionDecl: the FunctionLowerer works on a declaration, and building one here is
        // more honest than giving it a second entry point.
        var body = new Block(
            globals.Pending
                .Select(entry => (Stmt)new GlobalInitStmt(entry.Symbol, entry.Binding))
                .ToArray(),
            default);

        var decl = new FunctionDecl(
            IsPublic: false, IsMut: false, IsStatic: false, Name: Name, Generics: [],
            Parameters: [], ReturnType: null, Throws: null, Body: body, Span: default);

        return new FunctionLowerer(decl, Name, types, functions, imports, typeTable,
            ModuleLowerer.NoSubstitution, globals, lambdas, instances).Run();
    }
}

/// <summary>
/// "Fill this global slot with this initializer" — a statement that exists only in the synthetic
/// initializer.
///
/// <para>A node of its own rather than a repurposed <c>BindingStmt</c>: a <c>BindingStmt</c> creates a
/// LOCAL, and that is exactly what must not happen here. The difference in type makes the confusion
/// impossible instead of ruling it out through a condition in the lowerer.</para>
/// </summary>
internal sealed record GlobalInitStmt(GlobalSymbol Symbol, BindingStmt Binding)
    : Stmt(Binding.Span);
