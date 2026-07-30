using Lyric.Core;

namespace Lyric.Ir
{
    public enum IrScalar
    {
        I8, I16, I32, I64,
        U8, U16, U32, U64,
        F32, F64,
        Bool, Char, String, Void
    }

    public abstract record IrType
    {
        public static bool Equal(IrType a, IrType b)
        {
            switch (a, b)
            {
                case (IrScalarType x, IrScalarType y):
                    return x.Kind == y.Kind;
                default:
                    throw new InternalCompilationException($"ir-type: non scalar type cannot be compared");
            }
        }
    }

    public sealed record IrScalarType(IrScalar Kind) : IrType;


}