namespace Lyric.Bytecode;

/// <summary>
/// The attribute rows of a module, joined with the tables they point into — the form a host asks
/// questions of.
///
/// <para>Deliberately on the RAW module rather than on a loaded program: the module row is how a
/// host decides whether to load foreign bytes at all, so the query must not presuppose binding.
/// </para>
///
/// <para>Attribute names are the TYPE names of the bytecode and therefore unqualified. An SDK owns
/// its attribute names the way it owns its native names; two same-named attribute types from
/// different modules land under one name here.</para>
/// </summary>
public sealed class ModuleAttributes
{
    private readonly BytecodeModule _module;
    private readonly IReadOnlyList<AttributeUse> _all;

    private ModuleAttributes(BytecodeModule module, IReadOnlyList<AttributeUse> all)
    {
        _module = module;
        _all = all;
    }

    public static ModuleAttributes Of(BytecodeModule module)
    {
        var names = module.FieldNames.ToDictionary(entry => entry.Type, entry => entry.Names);
        var uses = new List<AttributeUse>(module.Attributes.Count);
        foreach (var row in module.Attributes)
        {
            var targetName = row.TargetKind switch
            {
                AttributeTargetKind.Function => module.Functions[row.Target].Name,
                AttributeTargetKind.Type => module.Types[row.Target].Name,
                _ => "",
            };
            uses.Add(new AttributeUse(
                module.Types[row.Type].Name,
                row.TargetKind, row.Target, targetName,
                names.GetValueOrDefault(row.Type) ?? [],
                row.Values));
        }
        return new ModuleAttributes(module, uses);
    }

    /// <summary>Every row, in the order the compiler wrote them: declaration order.</summary>
    public IReadOnlyList<AttributeUse> All => _all;

    /// <summary>The rows describing the module itself — identity, version, whatever the host's
    /// attribute vocabulary says. Answerable before any binding happens.</summary>
    public IEnumerable<AttributeUse> OnModule =>
        _all.Where(use => use.TargetKind == AttributeTargetKind.Module);

    /// <summary>The functions carrying <paramref name="attribute"/>. The target name is the
    /// QUALIFIED function name, and the target index is what a raw call path resolves once and
    /// keeps.</summary>
    public IEnumerable<AttributeUse> OnFunctions(string attribute) =>
        _all.Where(use => use.TargetKind == AttributeTargetKind.Function
                          && use.Attribute == attribute);

    /// <summary>The types carrying <paramref name="attribute"/>. <see cref="FieldsOf"/> turns a
    /// hit into the shape the host allocates for.</summary>
    public IEnumerable<AttributeUse> OnTypes(string attribute) =>
        _all.Where(use => use.TargetKind == AttributeTargetKind.Type
                          && use.Attribute == attribute);

    /// <summary>
    /// The shape of a type — the answer to what <c>@Component struct Health</c> declares.
    ///
    /// <para>Names exist only for types an attribute row references (section 12); for any other
    /// index this is <c>null</c>, not an empty list, so "no names were shipped" stays
    /// distinguishable from "the type has no fields".</para>
    /// </summary>
    public IReadOnlyList<AttributeField>? FieldsOf(int typeIndex)
    {
        var entry = _module.FieldNames.FirstOrDefault(n => n.Type == typeIndex);
        if (entry is null)
            return _module.Types[typeIndex].FieldTypes.Count == 0
                ? []
                : null;

        var opaque = _module.OpaqueFields.FirstOrDefault(o => o.Type == typeIndex)?.Names;
        return entry.Names
            .Select((name, i) => new AttributeField(name, _module.Types[typeIndex].FieldTypes[i],
                opaque is not null && opaque[i].Length > 0 ? opaque[i] : null))
            .ToArray();
    }
}

/// <summary>
/// One field of an attributed type: what it is called, what it is, and — since 3.5 — the name of
/// the <c>opaque type</c> it was declared with.
/// </summary>
/// <param name="OpaqueName">The declared opaque type, or <c>null</c> for every ordinary field and
/// for every module written before 3.5.
///
/// <para>It answers a question <see cref="Type"/> cannot: an <c>opaque type Entity = int</c> is an
/// <c>i64</c> below the sema, so a host that writes a save file sees a handle and a level number
/// as the same field. A handle must not be written — the slot it names belongs to someone else
/// after a restart — and this is what lets the host refuse it by name.</para>
///
/// <para>The LEAF name: a field of type <c>Entity[]</c> answers <c>Entity</c>, and
/// <see cref="Type"/> still says it is an array. A transparent alias resolves through to whatever
/// it names, because that one is not a type of its own.</para></param>
public sealed record AttributeField(string Name, BytecodeType Type, string? OpaqueName);

/// <summary>
/// One attribute on one target, with the values joined to their field names.
/// </summary>
public sealed class AttributeUse
{
    private readonly IReadOnlyList<string> _fields;

    internal AttributeUse(string attribute, AttributeTargetKind targetKind, int target,
        string targetName, IReadOnlyList<string> fields, IReadOnlyList<BytecodeConstValue> values)
    {
        Attribute = attribute;
        TargetKind = targetKind;
        Target = target;
        TargetName = targetName;
        _fields = fields;
        Values = values;
    }

    /// <summary>The attribute type's name, unqualified: <c>System</c>, not
    /// <c>engine.ecs.System</c>.</summary>
    public string Attribute { get; }

    public AttributeTargetKind TargetKind { get; }

    /// <summary>Function or type index depending on <see cref="TargetKind"/>; 0 for the module.
    /// For a function this is the index a host resolves once and calls with.</summary>
    public int Target { get; }

    /// <summary>The qualified function name, the type name, or empty for the module.</summary>
    public string TargetName { get; }

    /// <summary>One value per field of the attribute type, in field order — the row is complete,
    /// so a default the source never wrote is already in here.</summary>
    public IReadOnlyList<BytecodeConstValue> Values { get; }

    /// <summary>The value of the named field, or <c>null</c> when the attribute type has no such
    /// field. Name lookup requires the Names section, which every 3.2 compiler writes for
    /// referenced types.</summary>
    public BytecodeConstValue? Value(string field)
    {
        for (var i = 0; i < _fields.Count; i++)
            if (string.Equals(_fields[i], field, StringComparison.Ordinal))
                return Values[i];
        return null;
    }
}
