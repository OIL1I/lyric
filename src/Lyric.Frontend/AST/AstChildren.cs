namespace Lyric.AST;

/// <summary>
/// The children of a node, in source order.
///
/// <para>A <c>switch</c> rather than a visitor, and rather than reflection over the record members:
/// every case stands in one place, and the throwing <c>default</c> makes a new node type a build-time
/// question instead of a silent gap. A reflective walk would handle an added node without being
/// told, which sounds like an advantage until a node holds its children in a shape the walk does not
/// recognise — it would then be skipped, and whoever searches for the missing one finds nothing
/// wrong.</para>
///
/// <para>Source order matters. The consumer is a search for the node under a cursor, and it stops at
/// the first child that covers the offset; out of order, a later sibling could win over the one the
/// user is pointing at.</para>
///
/// <para>Leaves yield nothing rather than being absent from the switch: "this node has no children"
/// is an answer, and leaving it to the <c>default</c> would make it indistinguishable from "nobody
/// has said yet".</para>
/// </summary>
public static class AstChildren
{
    public static IEnumerable<Node> Of(Node node)
    {
        switch (node)
        {
            // --- module and declarations ---
            case Module m:
                if (m.Header is not null) yield return m.Header;
                foreach (var d in m.Declarations) yield return d;
                break;

            case ModulePath:
                break;

            case ImportDecl i:
                if (i.Clause is not null) yield return i.Clause;
                break;

            case ImportSelective:
            case ImportAlias:
                break;

            case GenericParam g:
                foreach (var c in g.Constraints) yield return c;
                break;

            case Param p:
                yield return p.Type;
                if (p.Default is not null) yield return p.Default;
                break;

            case ThrowsClause t:
                if (t.Type is not null) yield return t.Type;
                break;

            case FunctionDecl f:
                foreach (var g in f.Generics) yield return g;
                foreach (var p in f.Parameters) yield return p;
                if (f.ReturnType is not null) yield return f.ReturnType;
                if (f.Throws is not null) yield return f.Throws;
                if (f.Body is not null) yield return f.Body;
                break;

            case StaticBindingDecl s:
                yield return s.Binding;
                break;

            case FieldDecl f:
                yield return f.Type;
                if (f.Default is not null) yield return f.Default;
                break;

            case StructDecl s:
                foreach (var g in s.Generics) yield return g;
                foreach (var i in s.Interfaces) yield return i;
                foreach (var m in s.Members) yield return m;
                break;

            case ClassDecl c:
                foreach (var g in c.Generics) yield return g;
                foreach (var i in c.Interfaces) yield return i;
                foreach (var m in c.Members) yield return m;
                break;

            case EnumDecl e:
                foreach (var g in e.Generics) yield return g;
                foreach (var i in e.Interfaces) yield return i;
                foreach (var v in e.Variants) yield return v;
                foreach (var m in e.Methods) yield return m;
                break;

            case EnumVariant v:
                foreach (var t in v.TupleFields ?? []) yield return t;
                foreach (var f in v.StructFields ?? []) yield return f;
                break;

            case InterfaceDecl i:
                foreach (var g in i.Generics) yield return g;
                foreach (var m in i.Members) yield return m;
                break;

            case ExtendDecl e:
                yield return e.Target;
                foreach (var i in e.Interfaces) yield return i;
                foreach (var m in e.Methods) yield return m;
                break;

            case GlobalBindingDecl g:
                yield return g.Binding;
                break;

            case TypeAliasDecl a:
                yield return a.Aliased;
                break;

            case ErrorDecl:
                break;

            // --- statements ---
            case Block b:
                foreach (var s in b.Statements) yield return s;
                break;

            case BindingStmt b:
                if (b.Type is not null) yield return b.Type;
                if (b.Initializer is not null) yield return b.Initializer;
                break;

            case DestructuringStmt d:
                yield return d.Pattern;
                if (d.Type is not null) yield return d.Type;
                yield return d.Initializer;
                break;

            case IfStmt i:
                yield return i.Condition;
                yield return i.Then;
                if (i.Else is not null) yield return i.Else;
                break;

            case WhileStmt w:
                yield return w.Condition;
                yield return w.Body;
                break;

            case DoWhileStmt d:
                // The body is written first, and the search relies on that.
                yield return d.Body;
                yield return d.Condition;
                break;

            case ForInStmt f:
                yield return f.Iterable;
                yield return f.Body;
                break;

            case BreakStmt:
            case ContinueStmt:
                break;

            case ReturnStmt r:
                if (r.Value is not null) yield return r.Value;
                break;

            case YieldStmt y:
                if (y.Value is not null) yield return y.Value;
                break;

            case DeferStmt d:
                yield return d.Body;
                break;

            case ThrowStmt t:
                yield return t.Value;
                break;

            case MatchStmt m:
                yield return m.Scrutinee;
                foreach (var a in m.Arms) yield return a;
                break;

            case TryStmt t:
                yield return t.Body;
                foreach (var c in t.Catches) yield return c;
                break;

            case CatchClause c:
                if (c.BindingType is not null) yield return c.BindingType;
                yield return c.Body;
                break;

            case ExprStmt e:
                yield return e.Expr;
                break;

            case ErrorStmt:
                break;

            // --- expressions ---
            case IntLiteralExpr:
            case FloatLiteralExpr:
            case StringLiteralExpr:
            case CharLiteralExpr:
            case BoolLiteralExpr:
            case NullLiteralExpr:
            case IdentifierExpr:
            case ThisExpr:
            case ErrorExpr:
                break;

            case AtIdentifierExpr a:
                foreach (var arg in a.Arguments ?? []) yield return arg;
                break;

            case UnaryExpr u:
                yield return u.Operand;
                break;

            case ResumeExpr r:
                yield return r.Coroutine;
                break;

            case PostfixExpr p:
                yield return p.Operand;
                break;

            case BinaryExpr b:
                yield return b.Left;
                yield return b.Right;
                break;

            case AssignExpr a:
                yield return a.Target;
                yield return a.Value;
                break;

            case RangeExpr r:
                yield return r.Low;
                yield return r.High;
                break;

            case CastExpr c:
                yield return c.Operand;
                yield return c.Type;
                break;

            case CallExpr c:
                yield return c.Callee;
                foreach (var t in c.TypeArguments ?? []) yield return t;
                foreach (var a in c.Arguments) yield return a;
                break;

            case IndexExpr i:
                yield return i.Target;
                yield return i.Index;
                break;

            case MemberExpr m:
                // The member NAME is not a node of its own. Whoever wants it has to read it off this
                // node, which is why the search hands out the whole path rather than one node.
                yield return m.Target;
                break;

            case ArrayLitExpr a:
                foreach (var e in a.Elements) yield return e;
                break;

            case TupleLitExpr t:
                foreach (var e in t.Elements) yield return e;
                break;

            case InterpolatedStringExpr s:
                foreach (var segment in s.Segments) yield return segment;
                break;

            case InterpText:
                break;

            case InterpHole h:
                yield return h.Expr;
                break;

            case LambdaExpr l:
                foreach (var p in l.Parameters) yield return p;
                if (l.ReturnType is not null) yield return l.ReturnType;
                yield return l.Body;
                break;

            case LambdaParam p:
                if (p.Type is not null) yield return p.Type;
                break;

            case IfExpr i:
                yield return i.Condition;
                yield return i.Then;
                yield return i.Else;
                break;

            case MatchExpr m:
                yield return m.Scrutinee;
                foreach (var a in m.Arms) yield return a;
                break;

            case StructInitExpr s:
                foreach (var t in s.TypeArguments) yield return t;
                foreach (var f in s.Fields) yield return f;
                break;

            case StructInitField f:
                yield return f.Value;
                break;

            case TypePathExpr t:
                foreach (var a in t.TypeArguments) yield return a;
                break;

            // --- patterns ---
            case WildcardPattern:
            case BindingPattern:
            case ErrorPattern:
                break;

            case LiteralPattern l:
                yield return l.Literal;
                break;

            case VariantPattern v:
                foreach (var e in v.TupleElements ?? []) yield return e;
                foreach (var f in v.StructFields ?? []) yield return f;
                break;

            case TuplePattern t:
                foreach (var e in t.Elements) yield return e;
                break;

            case RangePattern r:
                yield return r.Low;
                yield return r.High;
                break;

            case OrPattern o:
                foreach (var a in o.Alternatives) yield return a;
                break;

            case FieldPattern f:
                if (f.Pattern is not null) yield return f.Pattern;
                break;

            case MatchArm a:
                yield return a.Pattern;
                if (a.Guard is not null) yield return a.Guard;
                yield return a.Body;
                break;

            // --- type expressions ---
            case NullableType n:
                yield return n.Inner;
                break;

            case NamedType n:
                foreach (var a in n.TypeArguments) yield return a;
                break;

            case ArrayType a:
                yield return a.Element;
                if (a.Size is not null) yield return a.Size;
                break;

            case TupleType t:
                foreach (var e in t.Elements) yield return e;
                break;

            case FunctionType f:
                foreach (var p in f.Parameters) yield return p;
                yield return f.ReturnType;
                break;

            case ErrorType:
                break;

            default:
                // Total over today's node types. A new one arrives here rather than being walked
                // past, and the message names what has to be decided.
                throw new NotSupportedException(
                    $"AstChildren has no case for {node.GetType().Name}; add one when the node is added");
        }
    }
}
