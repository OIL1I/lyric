using Lyric.AST;
using Lyric.Resolver;
using Lyric.Sema;

namespace Lyric.Ir.Lowering;

/// <summary>
/// Ordnet jedem gelowerten <c>class</c> eine <see cref="TypeId"/> zu und baut sein Layout.
///
/// <para><b>Interniert wird bei Bedarf</b>, wie bei <see cref="ImportTable"/>: eine deklarierte,
/// nie benutzte Klasse gehört nicht in die Typ-Tabelle des Bytecodes. Die Reihenfolge ergibt sich
/// aus der Lowering-Reihenfolge und ist damit deterministisch (ADR-013).</para>
///
/// <para><b>Die Id wird vor dem Layout vergeben.</b> Das ist der ganze Trick, der
/// <c>class Node { next: Node }</c> möglich macht: beim Betreten wird der Platz reserviert und die
/// Id eingetragen, erst danach werden die Feldtypen gelowert. Ein rekursiver Verweis findet die Id
/// dann schon vor und terminiert, statt sich selbst erneut zu internieren. Dieselbe Zwei-Phasen-
/// Form wie beim Funktions-Lowering (Pass 1 vergibt die <see cref="FunctionId"/>s, Pass 2 lowert),
/// und aus demselben Grund.</para>
///
/// <para><b>Feldreihenfolge kommt aus dem AST, nicht aus der Symboltabelle.</b> Der Feldindex ist
/// der Slot im Objekt — er muss die Deklarationsreihenfolge sein, und die garantiert nur die
/// AST-Liste. Eine Symboltabelle ist eine Map; sich auf ihre Aufzählungsreihenfolge zu verlassen
/// hieße, ein Layout an ein Implementierungsdetail zu hängen.</para>
/// </summary>
internal sealed class TypeTable
{
    private readonly Dictionary<TypeSymbol, TypeId> _assigned = new(ReferenceEqualityComparer.Instance);
    private readonly List<IrTypeDef> _defs = new();
    private readonly Dictionary<TypeSymbol, UnsupportedConstructException> _failed =
        new(ReferenceEqualityComparer.Instance);
    private readonly BindingResult _binding;

    /// <param name="binding">Der Resolver hat jeden <c>NamedType</c> bereits an sein Symbol
    /// gebunden. Diese Tabelle zu benutzen statt Namen selbst aufzulösen ist keine Bequemlichkeit:
    /// eine zweite Auflösung wäre eine zweite Wahrheit über Sichtbarkeit und Schattierung.</param>
    public TypeTable(BindingResult binding) => _binding = binding;

    public List<IrTypeDef> Defs => _defs;

    /// <summary>Der Typ eines Wertes dieser Klasse: eine Referenz, kein eingebetteter Wert
    /// (Sprache.md §3.3).</summary>
    public IrRefType RefTo(TypeSymbol symbol) => new(Intern(symbol));

    public TypeId Intern(TypeSymbol symbol)
    {
        // Ein Typ, dessen Layout schon einmal gescheitert ist, scheitert wieder — mit derselben
        // Meldung. Ohne das bliebe der Platzhalter aus dem ersten Versuch stehen, und der zweite
        // Aufrufer läse ein Layout mit FieldNames == null: eine NullReferenceException im Compiler
        // statt einer Diagnose. Genau das passierte bei `examples/bank.lyr`, dessen Account einen
        // Feld-Default hat.
        if (_failed.TryGetValue(symbol, out var failure))
            throw new UnsupportedConstructException(failure.Message, failure.Span);

        if (_assigned.TryGetValue(symbol, out var existing)) return existing;

        if (symbol.Kind != TypeSymbolKind.Class)
            throw new UnsupportedConstructException(
                $"type '{symbol.Name}' ({Describe(symbol.Kind)}) is not supported by this compiler version yet",
                SpanOf(symbol));

        if (symbol.Generics.Length > 0)
            throw new UnsupportedConstructException(
                $"generic type '{symbol.Name}' is not supported by this compiler version yet",
                SpanOf(symbol));

        if (symbol.Declaration is not ClassDecl decl)
            throw new UnsupportedConstructException(
                $"class '{symbol.Name}' has no declaration to read a layout from",
                SpanOf(symbol));

        // Platz reservieren UND Id eintragen, bevor die Feldtypen gelowert werden — siehe
        // Klassen-Doku. Der Platzhalter wird unten überschrieben; sichtbar wird er nie, weil
        // Lower(field) nur die Id braucht, nicht das Layout.
        var id = new TypeId(_defs.Count);
        _assigned[symbol] = id;
        _defs.Add(default);

        try
        {
            var fields = decl.Members.OfType<FieldDecl>().ToArray();
            var names = new string[fields.Length];
            var types = new IrType[fields.Length];

            for (var i = 0; i < fields.Length; i++)
            {
                if (fields[i].Default is not null)
                    throw new UnsupportedConstructException(
                        "a field default is not supported by this compiler version yet",
                        fields[i].Span);

                names[i] = fields[i].Name;
                types[i] = Lower(fields[i].Type, fields[i].Span);
            }

            _defs[id.Value] = new IrTypeDef(symbol.Name, types, names);
            return id;
        }
        catch (UnsupportedConstructException ex)
        {
            // Die Id NICHT zurückgeben: zwischenzeitlich kann ein Feldtyp weitere Typen interniert
            // haben, deren Ids sonst verschöben. Stattdessen den Fehlschlag merken — das Modul wird
            // ohnehin verworfen (ModuleLowerer liefert null), die Tabelle muss nur konsistent
            // bleiben, bis alle Funktionen ihre Diagnose abgesetzt haben.
            _failed[symbol] = ex;
            throw;
        }
    }

    /// <summary>Findet den Feldindex. Der Name existiert nur hier und in der Diagnose; im Bytecode
    /// steht ausschließlich die Position.</summary>
    public FieldId FieldOf(TypeSymbol symbol, string name, Core.Span span)
    {
        var def = _defs[Intern(symbol).Value];
        var index = Array.IndexOf(def.FieldNames, name);
        if (index >= 0) return new FieldId(index);

        throw new UnsupportedConstructException(
            $"member '{name}' of '{symbol.Name}' is not a field; only field access is supported " +
            "by this compiler version yet",
            span);
    }

    public IrType Lower(LyrType type, Core.Span span) => type switch
    {
        NamedRef { Symbol.Kind: TypeSymbolKind.Class } n => RefTo(n.Symbol),
        _ => TypeLowering.Lower(type)
    };

    /// <summary>
    /// Ein syntaktisch geschriebener Typ (Feld, Parameter, Rückgabetyp). Ein Klassentyp interniert
    /// rekursiv — das terminiert, weil <see cref="Intern"/> die Id vor dem Layout vergibt.
    /// </summary>
    public IrType Lower(TypeNode node) => Lower(node, node.Span);

    private IrType Lower(TypeNode node, Core.Span span)
    {
        // T[] (ADR-016). Eine Size im Typ gibt es nicht mehr — T[N] ist aus v1 gestrichen, die
        // Länge ist eine Eigenschaft des Wertes.
        if (node is ArrayType array)
        {
            if (array.Size is not null)
                throw new UnsupportedConstructException(
                    "a length in the array type ('T[N]') does not exist; the length belongs to the " +
                    "value — use 'T[]' and build it with '[x] * n'", node.Span);

            return new IrArrayType(Lower(array.Element, array.Element.Span));
        }

        if (node is NamedType { TypeArguments.Length: 0 } named)
        {
            if (TypeFacts.FromBuiltinName(named.Path[^1]) is { } primitive)
                return TypeLowering.Lower(primitive);

            var bound = _binding.Resolve(named);
            if (bound is ImportBindingSymbol import) bound = import.Target;
            if (bound is TypeSymbol type) return RefTo(type);
        }

        throw new UnsupportedConstructException(
            "a non-primitive field type is not supported by this compiler version yet", node.Span);
    }

    private static Core.Span SpanOf(TypeSymbol symbol) => symbol.Declaration?.Span ?? default;

    private static string Describe(TypeSymbolKind kind) => kind switch
    {
        TypeSymbolKind.Struct => "a struct; only classes are lowered",
        TypeSymbolKind.Enum => "an enum",
        TypeSymbolKind.Interface => "an interface",
        TypeSymbolKind.Alias => "a type alias",
        _ => "not a class"
    };
}
