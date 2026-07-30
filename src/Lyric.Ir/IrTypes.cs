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

    /// <summary>
    /// Basis der IR-Typen.
    /// </summary>
    public abstract record IrType
    {
        /// <summary>
        /// Typgleichheit zweier IR-Typen. Der <c>default</c>-Wurf ist Absicht und dieselbe
        /// Konvention wie bei <see cref="TypeLowering.Lower"/> („not lowerable in current version")
        /// und <c>IrPrinter.TypeStr</c> („not printable"): eine totale Funktion über das heutige
        /// Typ-Universum, die laut wird, sobald es wächst. Kommt ein zusammengesetzter Typ
        /// (Array, Tupel, Referenz) dazu, muss hier ein Fall her — der Wurf nennt die Stelle.
        /// </summary>
        /// <remarks>Ein <c>default</c>, der <c>false</c> liefert, wäre die schlechtere Wahl: der
        /// Verifier vergleicht Typen an rund zwanzig Stellen und hätte beim ersten nicht-skalaren
        /// Typ eine Flut falscher Typ-Mismatches gemeldet — der Fehler hätte nach IR-Bug
        /// ausgesehen statt nach Vergleichs-Bug.</remarks>
        public static bool Equal(IrType a, IrType b)
        {
            switch (a, b)
            {
                case (IrScalarType x, IrScalarType y):
                    return x.Kind == y.Kind;
                default:
                    throw new InternalCompilationException(
                        $"ir-type: cannot compare {a.GetType().Name} with {b.GetType().Name}");
            }
        }
    }

    public sealed record IrScalarType(IrScalar Kind) : IrType;
}