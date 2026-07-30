using Lyric.Core;
using Lyric.Ir;

namespace Lyric.Tests.Ir;

/// <summary>
/// Bausteine für absichtlich kaputtes IR. Getrennt von <see cref="Fixtures"/>, das per Vertrag
/// nur gültiges IR enthält.
///
/// Zwei Wege, ein defektes Modul zu bekommen:
/// <list type="bullet">
/// <item><see cref="Mutate"/> — eine gültige Fixture nehmen und <b>eine</b> Sache brechen. Der
/// Regelfall: der Test zeigt damit genau den Unterschied zwischen gültig und ungültig.</item>
/// <item>Die <c>Fn</c>/<c>Block</c>-Helfer — für Defekte, die per Mutation nicht erreichbar sind,
/// weil das Feld <c>init</c>-only ist (ReturnType, Block-Id) oder die Struktur leer sein muss.</item>
/// </list>
/// </summary>
internal static class BrokenIr
{
    public static readonly IrType I32 = new IrScalarType(IrScalar.I32);
    public static readonly IrType I64 = new IrScalarType(IrScalar.I64);
    public static readonly IrType U8 = new IrScalarType(IrScalar.U8);
    public static readonly IrType F32 = new IrScalarType(IrScalar.F32);
    public static readonly IrType F64 = new IrScalarType(IrScalar.F64);
    public static readonly IrType Bool = new IrScalarType(IrScalar.Bool);
    public static readonly IrType Str = new IrScalarType(IrScalar.String);

    // CharT/VoidT statt Char/Void: über 'using static' würden die mit System.Char und System.Void
    // kollidieren (CS0229).
    public static readonly IrType CharT = new IrScalarType(IrScalar.Char);
    public static readonly IrType VoidT = new IrScalarType(IrScalar.Void);
    public static readonly Span Sp = default;

    public static TempId T(int n) => new(n);
    public static LocalId L(int n) => new(n);
    public static BlockId B(int n) => new(n);
    public static FunctionId F(int n) => new(n);

    /// <summary>Nimmt eine gültige Fixture und wendet einen Defekt darauf an.</summary>
    public static IrModule Mutate(string fixture, Action<IrModule> breakIt)
    {
        var module = Fixtures.Build(fixture);
        breakIt(module);
        return module;
    }

    // Entry ist an IrFunction settable, defekte Entries werden deshalb per Mutate gebaut.
    public static IrFunction Fn(string name, IrType returnType, int paramCount,
        List<IrLocal> locals, List<IrTemp> temps, List<IrBlock> blocks)
        => new(name, returnType, paramCount, locals, temps, blocks) { Entry = B(0) };

    public static IrBlock Block(int id, List<IrOp> insts, IrTerminator? terminator)
        => new(B(id), insts) { Terminator = terminator };

    public static IrModule Module(params IrFunction[] functions) => new(functions.ToList());

    /// <summary>Ein IrOp-Typ, den der Verifier nicht kennt — für den Nachweis, dass der
    /// <c>default</c>-Zweig wirft statt stillschweigend durchzulassen.</summary>
    public sealed record UnknownOp(Span Span) : IrOp(Span);

    /// <summary>Dito für Terminatoren.</summary>
    public sealed record UnknownTerminator(Span Span) : IrTerminator(Span);
}
