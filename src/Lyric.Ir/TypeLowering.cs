using Lyric.Core;
using Lyric.Sema;

namespace Lyric.Ir
{
    public static class TypeLowering
    {
        public static IrType Lower(LyrType t)
        {
            switch (t)
            {
                case PrimitiveType pt:
                    return new IrScalarType(NormalizeScalar(pt.Kind));
                default:
                    throw new InternalCompilationException("ir: type not lowerable in current version: " +
                                                           TypeFacts.Display(t));
            }
        }

        private static IrScalar NormalizeScalar(PrimitiveKind k) => k switch
        {
            PrimitiveKind.Int => IrScalar.I64,
            PrimitiveKind.Uint => IrScalar.U64,
            PrimitiveKind.Float => IrScalar.F64,
            PrimitiveKind.Int8 => IrScalar.I8,
            PrimitiveKind.Uint8 => IrScalar.U8,
            PrimitiveKind.Int16 => IrScalar.I16,
            PrimitiveKind.Uint16 => IrScalar.U16,
            PrimitiveKind.Int32 => IrScalar.I32,
            PrimitiveKind.Uint32 => IrScalar.U32,
            PrimitiveKind.Int64 => IrScalar.I64,
            PrimitiveKind.Uint64 => IrScalar.U64,
            PrimitiveKind.Float32 => IrScalar.F32,
            PrimitiveKind.Float64 => IrScalar.F64,
            PrimitiveKind.Bool => IrScalar.Bool,
            PrimitiveKind.Char => IrScalar.Char,
            PrimitiveKind.String => IrScalar.String,
            PrimitiveKind.Void => IrScalar.Void,
            _ => throw new InternalCompilationException("ir: unknown primitive kind")
        };
    }
}