using Lyric.Core;

namespace Lyric.Ir;

/// <summary>
/// How IR terms are spelled: scalar names and op mnemonics in one place.
///
/// <para>A class of its own to avoid drift. <see cref="IrPrinter"/> needs the names for the dump,
/// <see cref="IrVerifier"/> for its findings. As long as both had their own mapping table, a rename
/// ("and" to "bitand") could make the verifier messages disagree with the printer output — and the two
/// get read side by side while hunting a lowering bug. Now both change at once.</para>
///
/// <para>All three methods throw in the <c>default</c> branch rather than yielding a substitute name:
/// an enum value without a name means an extension was not followed through, and that should show
/// rather than land in a snapshot as "I17". The same convention as <see cref="IrType.Equal"/> and
/// <see cref="TypeLowering.Lower"/>.</para>
/// </summary>
internal static class IrNames
{
    public static string Scalar(IrScalar kind) => kind switch
    {
        IrScalar.I8 => "i8",
        IrScalar.I16 => "i16",
        IrScalar.I32 => "i32",
        IrScalar.I64 => "i64",
        IrScalar.U8 => "u8",
        IrScalar.U16 => "u16",
        IrScalar.U32 => "u32",
        IrScalar.U64 => "u64",
        IrScalar.F32 => "f32",
        IrScalar.F64 => "f64",
        IrScalar.Bool => "bool",
        IrScalar.Char => "char",
        IrScalar.String => "string",
        IrScalar.Void => "void",
        _ => throw new InternalCompilationException($"ir-names: unknown scalar {kind}")
    };

    public static string Bin(IrBinKind kind) => kind switch
    {
        IrBinKind.Add => "add",
        IrBinKind.Sub => "sub",
        IrBinKind.Mul => "mul",
        IrBinKind.Div => "div",
        IrBinKind.Rem => "rem",
        IrBinKind.Shl => "shl",
        IrBinKind.Shr => "shr",
        IrBinKind.BitAnd => "and",
        IrBinKind.BitOr => "or",
        IrBinKind.BitXor => "xor",
        IrBinKind.Lt => "lt",
        IrBinKind.Le => "le",
        IrBinKind.Gt => "gt",
        IrBinKind.Ge => "ge",
        IrBinKind.Eq => "eq",
        IrBinKind.Ne => "ne",
        _ => throw new InternalCompilationException($"ir-names: unknown binop {kind}")
    };

    public static string Un(IrUnKind kind) => kind switch
    {
        IrUnKind.Neg => "neg",
        IrUnKind.Not => "not",
        IrUnKind.BitNot => "bitnot",
        _ => throw new InternalCompilationException($"ir-names: unknown unop {kind}")
    };
}
