using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Baut die synthetische Funktion, die alle globalen Slots fuellt.
///
/// <para>Sie sieht aus wie jede andere Funktion — keine Parameter, Rueckgabetyp <c>void</c>, ein
/// Block, ein <c>ret</c> — und wird auch so verifiziert und ausgefuehrt. Ihre Sonderrolle steht
/// ausschliesslich im Modul (<see cref="IrModule.GlobalInit"/>) und in der Zusage, dass eine
/// Runtime sie <b>vor</b> dem Einstiegspunkt ruft.</para>
///
/// <para><b>Warum eine Funktion und keine Werte in der Sektion.</b> Ein <c>static let ZERO:
/// Vector3 = Vector3 { … }</c> ist ein Ausdruck, kein Literal (ADR-014) — als Wert im Bytecode
/// waere er nur fuer Skalare darstellbar, und der Rest brauchte doch wieder Code. Eine Funktion
/// kann alles, was das Lowering ohnehin kann, und der Instruktionssatz bekommt keinen Sonderfall.
/// CIL loest es mit <c>.cctor</c> genauso.</para>
///
/// <para><b>Reihenfolge ist Deklarationsreihenfolge.</b> Ein Global darf ein frueher deklariertes
/// lesen, ein spaeteres nicht — das ergibt sich daraus, dass die Slots hier der Reihe nach
/// gefuellt werden, und ist die einzige Ordnung ohne Abhaengigkeitsanalyse. Wird ein spaeteres
/// gelesen, steht dort der Nullwert; das ist heute <b>nicht</b> geprueft und in STATUS vermerkt.</para>
/// </summary>
internal static class GlobalInitializer
{
    /// <summary>Der Name taucht im Bytecode auf und muss deshalb mit keinem Lyric-Bezeichner
    /// kollidieren koennen — <c>&lt;</c> ist in einem Identifier nicht erlaubt (§1.3).</summary>
    public const string Name = "<globals>";

    public static IrFunction Build(GlobalTable globals, TypeResult types,
        IReadOnlyDictionary<FunctionSymbol, FunctionId> functions, ImportTable imports,
        TypeTable typeTable)
    {
        // Ein synthetischer FunctionDecl: der FunctionLowerer arbeitet auf einer Deklaration, und
        // ihm hier eine zu bauen ist ehrlicher, als ihm einen zweiten Einstieg zu geben.
        var body = new Block(
            globals.Pending
                .Select(entry => (Stmt)new GlobalInitStmt(entry.Symbol, entry.Binding))
                .ToArray(),
            default);

        var decl = new FunctionDecl(
            IsPublic: false, IsMut: false, IsStatic: false, Name: Name, Generics: [],
            Parameters: [], ReturnType: null, Throws: null, Body: body, Span: default);

        return new FunctionLowerer(decl, Name, types, functions, imports, typeTable,
            ModuleLowerer.NoSubstitution, globals).Run();
    }
}

/// <summary>
/// „Fuelle diesen globalen Slot mit diesem Initialisierer" — ein Statement, das es nur im
/// synthetischen Initialisierer gibt.
///
/// <para>Ein eigener Knoten statt eines umgedeuteten <c>BindingStmt</c>: ein <c>BindingStmt</c>
/// legt ein <b>Local</b> an, und genau das darf hier nicht passieren. Der Unterschied im Typ
/// macht die Verwechslung unmoeglich, statt sie durch eine Bedingung im Lowerer auszuschliessen.</para>
/// </summary>
internal sealed record GlobalInitStmt(GlobalSymbol Symbol, BindingStmt Binding)
    : Stmt(Binding.Span);
