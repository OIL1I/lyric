using Lyric.Core;

namespace Lyric.Ir;

/// <summary>
/// Wie IR-Begriffe geschrieben werden: Skalar-Namen und Op-Mnemonics an einer Stelle.
///
/// <para>Der Grund für die eigene Klasse ist Drift-Vermeidung. <see cref="IrPrinter"/> braucht die
/// Namen für den Dump, <see cref="IrVerifier"/> für seine Befunde. Solange beide ihre eigene
/// Mapping-Tabelle hatten, konnte eine Umbenennung („and" → „bitand") die Verifier-Meldungen
/// gegen die Printer-Ausgabe laufen lassen — und man liest beide nebeneinander, wenn man einen
/// Lowering-Bug sucht. Jetzt ändern sich beide zugleich.</para>
///
/// <para>Alle drei Methoden werfen im <c>default</c>-Zweig statt einen Ersatznamen zu liefern:
/// ein Enum-Wert ohne Namen heißt, dass eine Erweiterung nicht nachgezogen wurde, und das soll
/// auffallen statt als „I17" im Snapshot zu landen. Gleiche Konvention wie
/// <see cref="IrType.Equal"/> und <see cref="TypeLowering.Lower"/>.</para>
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
