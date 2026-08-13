using Lyric.AST;

namespace Lyric.Resolver;

// Symbols are identity objects, without value equality, and are built incrementally
// aufgebaut: erst deklariert, in späteren Slices um Typ-Infos angereichert. Daher
// mutable Klassen, keine Records.

public enum Visibility
{
    Module, // Default: modul-privat
    Public  // 'pub'
}

public enum TypeSymbolKind
{
    Struct, Class, Enum, Interface, Alias, Builtin
}

public abstract class Symbol
{
    public string Name { get; }
    public Node? Declaration { get; } // AST-Knoten der Deklaration; null für Builtins/synthetische Symbole

    protected Symbol(string name, Node? declaration)
    {
        Name = name;
        Declaration = declaration;
    }
}

/// <summary>A module (one file). Members holds its top-level symbols.</summary>
public sealed class ModuleSymbol : Symbol
{
    public string[] Path { get; }
    public SymbolTable Members { get; }

    public ModuleSymbol(string[] path, SymbolTable members, Node? declaration = null)
        : base(path.Length > 0 ? path[^1] : "<root>", declaration)
    {
        Path = path;
        Members = members;
    }

    public string FullName => string.Join('.', Path);
}

/// <summary>struct / class / enum / interface / type-Alias / Builtin.</summary>
public sealed class TypeSymbol : Symbol
{
    public TypeSymbolKind Kind { get; }
    public Visibility Visibility { get; }
    public SymbolTable Members { get; } // Felder, Methoden, Enum-Varianten (leer bei Builtin/Alias)
    public GenericParamSymbol[] Generics { get; set; } = []; // Typ-Parameter (nach Deklaration gesetzt)

    public TypeSymbol(string name, TypeSymbolKind kind, Visibility visibility, SymbolTable members, Node? declaration)
        : base(name, declaration)
    {
        Kind = kind;
        Visibility = visibility;
        Members = members;
    }
}

public sealed class FunctionSymbol : Symbol
{
    public Visibility Visibility { get; }
    public bool IsMut { get; }

    /// <summary>A member without a receiver: no <c>this</c>, reachable only through the type.
    /// Bei freien Funktionen immer <c>false</c>.</summary>
    public bool IsStatic { get; }

    public GenericParamSymbol[] Generics { get; set; } = [];

    public FunctionSymbol(string name, Visibility visibility, bool isMut, Node? declaration,
        bool isStatic = false)
        : base(name, declaration)
    {
        Visibility = visibility;
        IsMut = isMut;
        IsStatic = isStatic;
    }
}

/// <summary>A generic type parameter (`T`) with its constraint interfaces, still as unresolved
/// TypeNodes; the sema resolves them.</summary>
public sealed class GenericParamSymbol : Symbol
{
    public TypeNode[] Constraints { get; }

    public GenericParamSymbol(string name, TypeNode[] constraints, Node? declaration) : base(name, declaration)
        => Constraints = constraints;
}

public sealed class FieldSymbol : Symbol
{
    public FieldSymbol(string name, Node? declaration) : base(name, declaration) { }
}

public sealed class EnumVariantSymbol : Symbol
{
    public EnumVariantSymbol(string name, Node? declaration) : base(name, declaration) { }
}

public sealed class GlobalSymbol : Symbol
{
    public Visibility Visibility { get; }

    public GlobalSymbol(string name, Visibility visibility, Node? declaration) : base(name, declaration)
    {
        Visibility = visibility;
    }
}

/// <summary>A name bound from an import whose target module is in the compilation: for a
/// namespace import the module itself, otherwise the imported symbol.</summary>
public sealed class ImportBindingSymbol : Symbol
{
    public Symbol Target { get; }

    public ImportBindingSymbol(string name, Symbol target, Node? declaration) : base(name, declaration)
    {
        Target = target;
    }
}

/// <summary>An import from a module outside the compilation.
/// gebaut). Opak: verhindert „unbekannter Name"-Fehler, trägt aber keine Struktur.</summary>
public sealed class ExternalSymbol : Symbol
{
    public string[] SourcePath { get; } // Modulpfad, aus dem es stammt

    public ExternalSymbol(string name, string[] sourcePath, Node? declaration) : base(name, declaration)
    {
        SourcePath = sourcePath;
    }
}

/// <summary>Recovery sentinel for names that cannot be resolved.</summary>
public sealed class ErrorSymbol : Symbol
{
    public ErrorSymbol(string name) : base(name, null) { }
}
