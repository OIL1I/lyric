using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Where an attribute list binds, and where it may not stand.
///
/// <para>The shape is settled by three rules. Before the module header it belongs to the MODULE;
/// at the top of a header-less file it belongs to the first declaration, because there is no
/// header it could describe. And it precedes <c>pub</c>, so the declaration span starts at the
/// first attribute.</para>
/// </summary>
public class AttributeParsingTests
{
    private static Module Parse(string source, out IReadOnlyList<Diagnostic> diagnostics)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        var module = new Parser(sm, id, de).ParseModule();
        diagnostics = de.Diagnostics;
        return module;
    }

    private static Module ParseClean(string source)
    {
        var module = Parse(source, out var diagnostics);
        Assert.Empty(diagnostics);
        return module;
    }

    [Fact]
    public void An_attribute_before_the_header_binds_to_the_module()
    {
        var module = ParseClean("@Plugin { name = \"m\", api = 2 }\nmodule m;\nfn f(): int { return 1; }");

        var attribute = Assert.Single(module.Attributes);
        Assert.Equal(["Plugin"], attribute.Path);
        Assert.Equal(2, attribute.Fields.Length);
        Assert.Empty(((FunctionDecl)module.Declarations[0]).Attributes);
    }

    [Fact]
    public void In_a_headerless_file_a_leading_attribute_binds_to_the_first_declaration()
    {
        var module = ParseClean("@Component\nstruct H { v: int }");

        Assert.Empty(module.Attributes);
        var s = Assert.IsType<StructDecl>(module.Declarations[0]);
        Assert.Equal(["Component"], Assert.Single(s.Attributes).Path);
    }

    [Fact]
    public void An_attribute_precedes_pub_and_the_declaration_span_starts_at_it()
    {
        var module = ParseClean("@System\npub fn tick(): void { }");

        var fn = Assert.IsType<FunctionDecl>(module.Declarations[0]);
        Assert.Single(fn.Attributes);
        Assert.Equal(0, fn.Span.Start);
    }

    [Fact]
    public void Several_attributes_stack_in_source_order()
    {
        var fn = (FunctionDecl)ParseClean("@A\n@B { x = 1 }\nfn f(): void { }").Declarations[0];

        Assert.Equal(2, fn.Attributes.Length);
        Assert.Equal(["A"], fn.Attributes[0].Path);
        Assert.Equal(["B"], fn.Attributes[1].Path);
    }

    [Fact]
    public void A_dotted_path_stays_one_attribute()
    {
        var fn = (FunctionDecl)ParseClean("@engine.ecs.System\nfn f(): void { }").Declarations[0];

        Assert.Equal(["engine", "ecs", "System"], Assert.Single(fn.Attributes).Path);
    }

    [Fact]
    public void The_path_span_excludes_the_argument_block()
    {
        var fn = (FunctionDecl)ParseClean("@System { order = 1 }\nfn f(): void { }").Declarations[0];
        var attribute = Assert.Single(fn.Attributes);

        Assert.Equal("@System".Length, attribute.PathSpan.End - attribute.PathSpan.Start);
        Assert.True(attribute.Span.End > attribute.PathSpan.End);
    }

    [Fact]
    public void A_class_and_an_enum_carry_attributes_too()
    {
        var module = ParseClean("@A\nclass C { }\n@B\nenum E { V }");

        Assert.Single(Assert.IsType<ClassDecl>(module.Declarations[0]).Attributes);
        Assert.Single(Assert.IsType<EnumDecl>(module.Declarations[1]).Attributes);
    }

    [Fact]
    public void An_attribute_on_an_import_is_rejected_and_the_import_survives()
    {
        var module = Parse("@A\nimport std.core { Equatable };\nfn f(): void { }", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "LYR-PAR0042");
        Assert.IsType<ImportDecl>(module.Declarations[0]);
        Assert.IsType<FunctionDecl>(module.Declarations[1]);
    }

    [Fact]
    public void An_attribute_on_a_member_parses_and_attaches()
    {
        // Since 2.1 the member CARRIES the list; which attributes are admissible there is the
        // sema's call (only @Deprecated), not the grammar's.
        var module = Parse("struct S { @A\nv: int }", out var diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Code == "LYR-PAR0042");
        var s = Assert.IsType<StructDecl>(module.Declarations[0]);
        var field = Assert.IsType<FieldDecl>(Assert.Single(s.Members));
        Assert.Equal("v", field.Name);
        Assert.Equal("A", Assert.Single(field.Attributes).Path.Single());
    }

    [Fact]
    public void An_attribute_on_an_interface_member_stays_rejected()
    {
        var module = Parse("interface I { @A\nfn f(): int; }", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "LYR-PAR0042");
        var i = Assert.IsType<InterfaceDecl>(module.Declarations[0]);
        Assert.Equal("f", Assert.Single(i.Members).Name); // the member survives the rejection
    }

    [Fact]
    public void An_attribute_on_an_extend_block_is_rejected()
    {
        Parse("@A\nextend int { fn f(): int { return 1; } }", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "LYR-PAR0042");
    }

    /// <summary>The old expression form <c>@name(args)</c> is out of the grammar: at declaration
    /// position the '(' no longer belongs to the attribute. What follows fails to open a
    /// declaration, and the message says what an attribute needs.</summary>
    [Fact]
    public void The_reserved_call_form_is_no_longer_attribute_syntax()
    {
        Parse("@deprecated(\"old\")\nfn f(): void { }", out var diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "LYR-PAR0042");
    }

    [Fact]
    public void AstChildren_walks_the_attribute_and_its_fields()
    {
        var module = ParseClean("@System { order = 1 }\nfn f(): void { }");
        var fn = (FunctionDecl)module.Declarations[0];
        var attribute = Assert.Single(fn.Attributes);

        Assert.Contains(attribute, AstChildren.Of(fn));
        var field = Assert.Single(attribute.Fields);
        Assert.Contains(field, AstChildren.Of(attribute));
        Assert.Contains(field.Value, AstChildren.Of(field));
    }

    [Fact]
    public void Module_attributes_are_children_of_the_module_node()
    {
        var module = ParseClean("@Plugin\nmodule m;\nfn f(): void { }");

        Assert.Contains(module.Attributes[0], AstChildren.Of(module));
    }
}
