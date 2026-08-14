using Lyric.Core;
using Lyric.Ir;

namespace Lyric.Tests.Ir;

/// <summary>
/// Building blocks for deliberately broken IR. Separate from <see cref="Fixtures"/>, which by contract
/// contains valid IR only.
///
/// Two ways to get a defective module:
/// <list type="bullet">
/// <item><see cref="Mutate"/> — take a valid fixture and break ONE thing. The regular case: the test
/// then shows exactly the difference between valid and invalid.</item>
/// <item>The <c>Fn</c> and <c>Block</c> helpers — for defects unreachable by mutation, because the field
/// is <c>init</c>-only (ReturnType, block id) or the structure has to be empty.</item>
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

    // CharT and VoidT rather than Char and Void: through 'using static' those would collide with
    // System.Char and System.Void (CS0229).
    public static readonly IrType CharT = new IrScalarType(IrScalar.Char);
    public static readonly IrType VoidT = new IrScalarType(IrScalar.Void);
    public static readonly Span Sp = default;

    public static TempId T(int n) => new(n);
    public static LocalId L(int n) => new(n);
    public static BlockId B(int n) => new(n);
    public static FunctionId F(int n) => new(n);
    public static TypeId Ty(int n) => new(n);
    public static FieldId Fld(int n) => new(n);
    public static IrType Ref(int n) => new IrRefType(Ty(n));

    /// <summary>A module with a type table. <c>Module(params …)</c> stays beside it, so the existing tests
    /// read unchanged.</summary>
    public static IrModule ModuleWithTypes(List<IrTypeDef> types, params IrFunction[] functions)
        => new(functions.ToList()) { Types = types };

    public static IrTypeDef TypeDef(string name, params (string Name, IrType Type)[] fields)
        => new(name, fields.Select(f => f.Type).ToArray(), fields.Select(f => f.Name).ToArray());

    /// <summary>Takes a valid fixture and applies a defect to it.</summary>
    public static IrModule Mutate(string fixture, Action<IrModule> breakIt)
    {
        var module = Fixtures.Build(fixture);
        breakIt(module);
        return module;
    }

    // Entry is settable on IrFunction, so defective entries are built through Mutate.
    public static IrFunction Fn(string name, IrType returnType, int paramCount,
        List<IrLocal> locals, List<IrTemp> temps, List<IrBlock> blocks)
        => new(name, returnType, paramCount, locals, temps, blocks) { Entry = B(0) };

    public static IrBlock Block(int id, List<IrOp> insts, IrTerminator? terminator)
        => new(B(id), insts) { Terminator = terminator };

    public static IrModule Module(params IrFunction[] functions) => new(functions.ToList());

    /// <summary>An IrOp type the verifier does not know, to show that the
    /// <c>default</c>-Zweig wirft statt stillschweigend durchzulassen.</summary>
    public sealed record UnknownOp(Span Span) : IrOp(Span);

    /// <summary>The same for terminators.</summary>
    public sealed record UnknownTerminator(Span Span) : IrTerminator(Span);
}
