using Lyric.AST;
using Lyric.Core;

namespace Lyric.Resolver;

/// <summary>
/// The built-in types plus the language built-ins `Throwable` (an interface with an abstract
/// `message(): string`) and `panic` (return type never).
///
/// They live in a scope that is the root parent of every module scope, so a name resolves through
/// the ordinary lookup chain with no special case in the resolver.
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

    /// <summary>Creates a fresh scope holding every built-in type symbol.</summary>
    public static SymbolTable CreateScope()
    {
        var scope = new SymbolTable();
        foreach (var name in Names)
            scope.TryDeclare(new TypeSymbol(name, TypeSymbolKind.Builtin, Visibility.Public,
                new SymbolTable(), declaration: null));
        scope.TryDeclare(CreateThrowable());
        scope.TryDeclare(CreatePanic());
        // Coroutine<T>: the name resolves here, the type form is built by the sema.
        scope.TryDeclare(new TypeSymbol("Coroutine", TypeSymbolKind.Builtin, Visibility.Public,
            new SymbolTable(), declaration: null));
        return scope;
    }

    // `Throwable` as a real interface symbol with a synthetic AST, so the conformance check and
    // member lookup run through the ordinary paths.
    private static TypeSymbol CreateThrowable()
    {
        // Nothing here stands in a file, so both spans are the default one and carry an invalid
        // FileId. A consumer that offers to jump to a declaration checks for that already.
        var message = new FunctionDecl(
            IsPublic: true, IsMut: false, IsStatic: false, Name: "message", Generics: [], Parameters: [],
            ReturnType: new NamedType(["string"], [], default) { NameSpan = default },
            Throws: null, Body: null, Span: default)
            { NameSpan = default };
        var decl = new InterfaceDecl(IsPublic: true, Name: "Throwable", Generics: [], Members: [message], Span: default)
            { NameSpan = default };
        var members = new SymbolTable();
        members.TryDeclare(new FunctionSymbol("message", Visibility.Public, isMut: false, message));
        return new TypeSymbol("Throwable", TypeSymbolKind.Interface, Visibility.Public, members, decl);
    }

    // `panic(message: string)`: the never return type is not nameable, so the sema sets it for
    // this symbol directly.
    private static FunctionSymbol CreatePanic()
    {
        var decl = new FunctionDecl(
            IsPublic: true, IsMut: false, IsStatic: false, Name: "panic", Generics: [],
            Parameters: [new Param(IsParams: false, Name: "message",
                Type: new NamedType(["string"], [], default) { NameSpan = default },
                Default: null, Span: default)
                { NameSpan = default }],
            ReturnType: null, Throws: null, Body: null, Span: default) { NameSpan = default };
        return new FunctionSymbol("panic", Visibility.Public, isMut: false, decl);
    }

    public static bool IsBuiltin(string name) => Array.IndexOf(Names, name) >= 0;
}
