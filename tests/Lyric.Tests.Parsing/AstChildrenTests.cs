using System.Reflection;
using System.Runtime.CompilerServices;
using Lyric.AST;
using Lyric.Core;

namespace Lyric.Tests.Parsing;

/// <summary>
/// The children enumeration, and above all that it is TOTAL.
///
/// <para>The totality check is mechanical rather than a list someone maintains: a list would be the
/// second description of the node set and would drift from the first. Reflection asks the assembly
/// which nodes exist, so adding a node makes this test fail until the walk is told about it — which
/// is the whole reason the <c>default</c> throws instead of yielding nothing.</para>
/// </summary>
public sealed class AstChildrenTests
{
    /// <summary>
    /// Every concrete SYNTAX node: the ones a parser produces.
    ///
    /// <para>Restricted to the <c>Lyric.AST</c> namespace, and that restriction is the point rather
    /// than a convenience. Later stages derive nodes of their own from <see cref="Node"/> —
    /// <c>GlobalInitStmt</c> is a statement the lowering synthesises — and they are not syntax. A
    /// case for one in <c>AstChildren</c> would mean the AST knows about the lowering, which is the
    /// wrong direction; meeting one during a syntax walk means something leaked, and throwing is
    /// then the right answer. <see cref="A_node_that_is_not_syntax_is_refused"/> holds that.</para>
    /// </summary>
    public static TheoryData<Type> AllNodeTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(Node).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsAssignableTo(typeof(Node))) continue;
            if (type.Namespace != typeof(Node).Namespace) continue;
            data.Add(type);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllNodeTypes))]
    public void Every_node_type_has_a_case(Type type)
    {
        // Uninitialized rather than constructed: most nodes need a dozen arguments, and what is
        // being asked here is only whether the switch KNOWS the type. A node whose arrays are null
        // throws a NullReferenceException while walking, and that answers the question with yes.
        var node = (Node)RuntimeHelpers.GetUninitializedObject(type);

        try
        {
            _ = AstChildren.Of(node).ToList();
        }
        catch (NotSupportedException exception)
        {
            Assert.Fail($"AstChildren has no case for {type.Name}: {exception.Message}");
        }
        catch (NullReferenceException)
        {
            // The case exists and walked into a member this instance does not have. That is the
            // cost of not constructing the node, and it is not what this test is about.
        }
    }

    [Fact]
    public void The_reflection_actually_found_the_nodes()
    {
        // Without this the theory above is green when the search breaks and yields nothing —
        // "no failures" and "nothing was checked" look identical in a test report.
        Assert.True(AllNodeTypes().Count > 50,
            $"expected the AST to have more than 50 concrete node types, found {AllNodeTypes().Count}");
    }

    [Fact]
    public void Children_come_in_source_order()
    {
        // The consumer descends into the first child covering an offset, so an order that does not
        // match the source would hand out a sibling instead of the node under the cursor.
        var file = new FileId(1);
        Span At(int start, int end) => new(file, start, end);

        var condition = new IdentifierExpr("c", At(4, 5));
        var body = new Block([], At(7, 9));
        var loop = new WhileStmt(condition, body, At(0, 9));

        var children = AstChildren.Of(loop).ToList();

        Assert.Equal([condition, body], children);
    }

    [Fact]
    public void A_do_while_yields_its_body_before_its_condition()
    {
        // The one node whose members are declared in the opposite order to the source. Written down
        // because the natural implementation follows the record and would be wrong here.
        var file = new FileId(1);
        var body = new Block([], new Span(file, 3, 5));
        var condition = new IdentifierExpr("c", new Span(file, 13, 14));

        var children = AstChildren.Of(new DoWhileStmt(body, condition, new Span(file, 0, 16))).ToList();

        Assert.Equal([body, condition], children);
    }

    [Fact]
    public void A_node_that_is_not_syntax_is_refused()
    {
        // The counterpart of the theory above. A node from a later stage reaching a syntax walk is
        // a leak, and the walk says so instead of returning "no children" — which would read as a
        // leaf and hide whatever put it there.
        var foreign = typeof(Node).Assembly.GetTypes().FirstOrDefault(
            t => !t.IsAbstract
                 && t.IsAssignableTo(typeof(Node))
                 && t.Namespace != typeof(Node).Namespace);

        Assert.NotNull(foreign);

        var node = (Node)RuntimeHelpers.GetUninitializedObject(foreign);

        Assert.Throws<NotSupportedException>(() => AstChildren.Of(node).ToList());
    }

    [Fact]
    public void A_leaf_yields_nothing_rather_than_throwing()
    {
        var identifier = new IdentifierExpr("x", new Span(new FileId(1), 0, 1));

        Assert.Empty(AstChildren.Of(identifier));
    }

    [Fact]
    public void An_optional_child_that_is_absent_is_skipped()
    {
        var file = new FileId(1);
        var withoutValue = new ReturnStmt(null, new Span(file, 0, 7));
        var value = new IntLiteralExpr(1, null, new Span(file, 7, 8));

        Assert.Empty(AstChildren.Of(withoutValue));
        Assert.Equal([value], AstChildren.Of(new ReturnStmt(value, new Span(file, 0, 9))).ToList());
    }
}
