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
    /// <summary>An index into <c>IrModule.Imports</c>. Separate from <see cref="FunctionId"/>, because
    /// in the IR these are two different things: one has a body, the other does not. The shared index
    /// space of the bytecode — imports first, then functions — is formed by the writer.</summary>
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

    /// <summary>An index into <c>IrModule.Types</c>. As with every id here the value is the table index
    /// in the later bytecode, not merely a key.</summary>
    public readonly record struct TypeId(int Value)
    {
        public override string ToString()
        {
            return $"ty{Value}";
        }
    }

    /// <summary>The position of a field in the layout of its type. Field names do not appear in the
    /// bytecode: the access is an offset, the name is metadata.</summary>
    public readonly record struct FieldId(int Value)
    {
        public override string ToString()
        {
            return $"#{Value}";
        }
    }
}

/// <summary>An index into <see cref="IrModule.Globals"/>. Like every id here it IS the slot in the
/// bytecode, which is why the table is dense.</summary>
public readonly record struct GlobalId(int Value)
{
    public override string ToString() => $"g{Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
