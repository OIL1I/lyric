namespace Lyric.Ir
{
    public readonly record struct BlockId(int Value)
    {
        public override string ToString()
        {
            return $"bb{Value}";
        }

    }

    public readonly record struct TempId(int Value)
    {
        public override string ToString()
        {
            return $"t{Value}";
        }
    }

    public readonly record struct LocalId(int Value)
    {
        public override string ToString()
        {
            return $"l{Value}";
        }
    }
    /// <summary>Index in <c>IrModule.Imports</c>. Getrennt von <see cref="FunctionId"/>, weil das
    /// in der IR zwei verschiedene Dinge sind: eines hat einen Rumpf, das andere nicht. Den
    /// gemeinsamen Indexraum des Bytecodes (erst Imports, dann Funktionen) bildet erst der Writer.</summary>
    public readonly record struct ImportId(int Value)
    {
        public override string ToString()
        {
            return $"i{Value}";
        }
    }

    public readonly record struct FunctionId(int Value)
    {
        public override string ToString()
        {
            return $"f{Value}";
        }
    }

    /// <summary>Index in <c>IrModule.Types</c>. Wie alle Ids hier ist der Wert der Tabellen-Index
    /// im späteren Bytecode, nicht nur ein Schlüssel.</summary>
    public readonly record struct TypeId(int Value)
    {
        public override string ToString()
        {
            return $"ty{Value}";
        }
    }

    /// <summary>Position eines Feldes im Layout seines Typs. Feldnamen stehen nicht im Bytecode —
    /// der Zugriff ist ein Offset, der Name Metadaten.</summary>
    public readonly record struct FieldId(int Value)
    {
        public override string ToString()
        {
            return $"#{Value}";
        }
    }
}

/// <summary>Index in <see cref="IrModule.Globals"/>. Wie jede Id hier: sie <b>ist</b> der Slot im
/// Bytecode, die Tabelle ist deshalb dicht.</summary>
public readonly record struct GlobalId(int Value)
{
    public override string ToString() => $"g{Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
