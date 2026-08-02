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
}