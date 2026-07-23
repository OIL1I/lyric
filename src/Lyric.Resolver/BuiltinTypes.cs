using Lyric.AST;
using Lyric.Core;

namespace Lyric.Resolver;

/// <summary>
/// Die eingebauten Typen (Sprache.md §4) plus die Sprach-Built-ins `Throwable` (§9,
/// Interface mit abstraktem `message(): string`) und `panic` (§9, Rückgabetyp never).
/// Sie leben in einem Scope, der Wurzel-Parent jedes Modul-Scopes ist — so löst ein
/// `int`/`Throwable`/… über die normale Lookup-Kette auf, ohne Sonderfall im Resolver.
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
        scope.TryDeclare(CreateThrowable());
        scope.TryDeclare(CreatePanic());
        return scope;
    }

    // `Throwable` als echtes Interface-Symbol mit synthetischem AST: so laufen
    // Konformanz-Check (message() ist abstrakt) und Member-Lookup (e.message() auf
    // Catch-All-Bindungen) über die normalen Wege, ohne Sonderfälle in der Sema.
    private static TypeSymbol CreateThrowable()
    {
        var message = new FunctionDecl(
            IsPublic: true, IsMut: false, Name: "message", Generics: [], Parameters: [],
            ReturnType: new NamedType(["string"], [], default), Throws: null, Body: null, Span: default);
        var decl = new InterfaceDecl(IsPublic: true, Name: "Throwable", Generics: [], Members: [message], Span: default);
        var members = new SymbolTable();
        members.TryDeclare(new FunctionSymbol("message", Visibility.Public, isMut: false, message));
        return new TypeSymbol("Throwable", TypeSymbolKind.Interface, Visibility.Public, members, decl);
    }

    // `panic(message: string)`: der never-Rückgabetyp ist nicht benennbar (kein §4-Typ),
    // die Sema setzt ihn für dieses Symbol direkt.
    private static FunctionSymbol CreatePanic()
    {
        var decl = new FunctionDecl(
            IsPublic: true, IsMut: false, Name: "panic", Generics: [],
            Parameters: [new Param(IsParams: false, Name: "message",
                Type: new NamedType(["string"], [], default), Default: null, Span: default)],
            ReturnType: null, Throws: null, Body: null, Span: default);
        return new FunctionSymbol("panic", Visibility.Public, isMut: false, decl);
    }

    public static bool IsBuiltin(string name) => Array.IndexOf(Names, name) >= 0;
}
