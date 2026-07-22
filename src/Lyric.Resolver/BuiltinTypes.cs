namespace Lyric.Resolver;

/// <summary>
/// Die eingebauten Typen (Sprache.md §4). Sie leben in einem Scope, der Wurzel-Parent
/// jedes Modul-Scopes ist — so löst ein `int`/`string`/… über die normale Lookup-Kette
/// auf, ohne Sonderfall im Resolver.
/// </summary>
public static class BuiltinTypes
{
    public static readonly string[] Names =
    {
        "int", "uint", "float",
        "int8", "int16", "int32", "int64",
        "uint8", "uint16", "uint32", "uint64",
        "float32", "float64",
        "bool", "char", "string", "void"
    };

    /// <summary>Erzeugt einen frischen Scope mit allen Builtin-TypeSymbols.</summary>
    public static SymbolTable CreateScope()
    {
        var scope = new SymbolTable();
        foreach (var name in Names)
            scope.TryDeclare(new TypeSymbol(name, TypeSymbolKind.Builtin, Visibility.Public,
                new SymbolTable(), declaration: null));
        return scope;
    }

    public static bool IsBuiltin(string name) => Array.IndexOf(Names, name) >= 0;
}
