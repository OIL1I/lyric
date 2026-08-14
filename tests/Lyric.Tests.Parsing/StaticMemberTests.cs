using Lyric.AST;
using Lyric.Core;
using Lyric.Parsing;
using Xunit;

namespace Lyric.Tests.Parsing;

/// <summary>
/// Where <c>static</c> may stand on a member.
///
/// <para>The grammar puts <c>static</c> into <c>FunctionDecl</c>, and an enum, interface and extend
/// body all contain <c>FunctionDecl</c> — but only a struct or class body ever read it, so all three
/// gave <c>LYR-PAR0008</c> plus two follow-ups, none of them about the cause.</para>
///
/// <para>An interface is the exception and stays rejected: its members are dispatched through a
/// vtable slot, which takes a receiver, and a static member has none. Accepting it there produced a
/// VERIFIER CRASH once the type was used as an interface value — worse than the parse error it
/// replaced, and in Release, where the verifier may be off, it would be malformed bytecode.</para>
/// </summary>
public class StaticMemberTests
{
    private static (Module module, DiagnosticEngine diag) Parse(string source)
    {
        var sm = new SourceManager();
        var id = sm.AddVirtual("test.lyr", source);
        var de = new DiagnosticEngine(sm);
        return (new Parser(sm, id, de).ParseModule(), de);
    }

    private static string[] Codes(DiagnosticEngine de) =>
        de.Diagnostics.Select(d => d.Code).ToArray();

    // ------------------------------------------------------------------ accepted

    [Fact]
    public void An_enum_method_may_be_static()
    {
        var (module, de) = Parse("enum E { A; static fn make(): E { return E.A; } }");
        Assert.False(de.HasErrors);
        var e = Assert.IsType<EnumDecl>(module.Declarations[0]);
        Assert.True(Assert.Single(e.Methods).IsStatic);
    }

    [Fact]
    public void An_extend_method_may_be_static()
    {
        var (module, de) = Parse("extend int { static fn zero(): int { return 0; } }");
        Assert.False(de.HasErrors);
        var x = Assert.IsType<ExtendDecl>(module.Declarations[0]);
        Assert.True(Assert.Single(x.Methods).IsStatic);
    }

    [Fact]
    public void The_modifiers_keep_the_order_of_the_grammar()
    {
        // 'pub' then 'static' then 'mut' then 'fn'. 'mut static' does not exist.
        var (module, de) = Parse("enum E { A; pub static fn make(): E { return E.A; } }");
        Assert.False(de.HasErrors);
        var m = Assert.Single(Assert.IsType<EnumDecl>(module.Declarations[0]).Methods);
        Assert.True(m.IsPublic);
        Assert.True(m.IsStatic);
        Assert.False(m.IsMut);
    }

    [Fact]
    public void A_static_method_stands_beside_instance_methods()
    {
        var (module, de) = Parse(
            "enum E { A; fn tag(): int { return 1; } static fn first(): E { return E.A; } }");
        Assert.False(de.HasErrors);
        var methods = Assert.IsType<EnumDecl>(module.Declarations[0]).Methods;
        Assert.Equal([false, true], methods.Select(m => m.IsStatic));
    }

    [Fact]
    public void A_member_without_static_is_not_marked_static()
    {
        // The counter-check: a parser that set the flag unconditionally would pass the tests above.
        var (module, _) = Parse("enum E { A; fn tag(): int { return 1; } }");
        Assert.False(Assert.Single(Assert.IsType<EnumDecl>(module.Declarations[0]).Methods).IsStatic);
    }

    [Fact]
    public void A_class_and_a_struct_body_are_untouched()
    {
        var (module, de) = Parse(
            "class K { static let n: int = 3; static fn get(): int { return K.n; } }\n"
            + "struct S { v: int, static fn make(): S { return S { v = 1 }; } }");
        Assert.False(de.HasErrors);
        Assert.IsType<ClassDecl>(module.Declarations[0]);
        Assert.IsType<StructDecl>(module.Declarations[1]);
    }

    // ------------------------------------------------------------------ rejected

    [Fact]
    public void An_interface_member_cannot_be_static()
    {
        var (_, de) = Parse("interface I { static fn make(): int; }");
        Assert.Equal(["LYR-PAR0041"], Codes(de));
    }

    [Fact]
    public void The_interface_message_names_the_way_out()
    {
        var (_, de) = Parse("interface I { static fn make(): int; }");
        var message = Assert.Single(de.Diagnostics).Message;
        Assert.Contains("receiver", message);
        Assert.Contains("implementing type", message);
    }

    [Fact]
    public void The_rejected_interface_member_is_kept_as_an_instance_member()
    {
        // Reading on rather than skipping: the rest of the member is well formed, and stopping would
        // report every following member as well.
        var (module, _) = Parse("interface I { static fn a(): int; fn b(): int; }");
        var members = Assert.IsType<InterfaceDecl>(module.Declarations[0]).Members;
        Assert.Equal(["a", "b"], members.Select(m => m.Name));
        Assert.All(members, m => Assert.False(m.IsStatic));
    }

    [Fact]
    public void Two_static_interface_members_give_two_messages_and_no_cascade()
    {
        var (_, de) = Parse("interface I { static fn a(): int; static fn b(): int; }");
        Assert.Equal(["LYR-PAR0041", "LYR-PAR0041"], Codes(de));
    }

    [Theory]
    [InlineData("enum E { A; static let x: int = 1; }")]
    [InlineData("interface I { static let x: int = 1; }")]
    [InlineData("extend int { static let x: int = 1; }")]
    public void A_static_binding_belongs_to_a_struct_or_class_body_only(string source)
    {
        // It used to fall into ParseFunctionDecl, which failed on the missing 'fn' and reported
        // through the rest of the file — 21 messages for one cause in the enum case.
        var (_, de) = Parse(source);
        Assert.Equal(["LYR-PAR0040"], Codes(de));
    }

    [Fact]
    public void A_rejected_static_binding_does_not_swallow_the_next_member()
    {
        var (module, de) = Parse("enum E { A; static let x: int = 1; fn tag(): int { return 1; } }");
        Assert.Equal(["LYR-PAR0040"], Codes(de));
        Assert.Equal(["tag"], Assert.IsType<EnumDecl>(module.Declarations[0]).Methods.Select(m => m.Name));
    }
}
