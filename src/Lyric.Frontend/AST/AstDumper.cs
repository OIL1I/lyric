using System.Globalization;
using System.Text;
using Lyric.Core;

namespace Lyric.AST;

/// <summary>
/// A deterministic tree dump of an AST node for golden snapshots and debugging.
///
/// Child order is fixed and positional, with no role labels.
///
/// A <c>switch</c> rather than a visitor: every case in one place, and the throwing
/// <c>default</c> forces completeness for a new node.
/// </summary>
public static class AstDumper
{
    public static string Dump(Node node, SourceManager sources)
    {
        var sb = new StringBuilder();
        Write(node, 0, sb);
        return sb.ToString();
    }

    private static void Write(Node node, int indent, StringBuilder sb)
    {
        switch (node)
        {
            // --- literals ---
            case IntLiteralExpr n:
                Line(sb, indent, $"Int {n.Value}{Suffix(n.Suffix)}", n.Span);
                break;
            case FloatLiteralExpr n:
                Line(sb, indent, $"Float {n.Value.ToString("R", CultureInfo.InvariantCulture)}{Suffix(n.Suffix)}", n.Span);
                break;
            case StringLiteralExpr n:
                Line(sb, indent, $"String {Quote(n.Value)}", n.Span);
                break;
            case CharLiteralExpr n:
                Line(sb, indent, $"Char {n.CodePoint}", n.Span);
                break;
            case BoolLiteralExpr n:
                Line(sb, indent, $"Bool {(n.Value ? "true" : "false")}", n.Span);
                break;
            case NullLiteralExpr n:
                Line(sb, indent, "Null", n.Span);
                break;

            // --- names ---
            case IdentifierExpr n:
                Line(sb, indent, $"Ident {n.Name}", n.Span);
                break;
            case AtIdentifierExpr n:
                Line(sb, indent, $"AtIdent {n.Name}{(n.Arguments is null ? "" : " (call)")}", n.Span);
                foreach (var a in n.Arguments ?? []) Write(a, indent + 1, sb);
                break;
            case ThisExpr n:
                Line(sb, indent, "This", n.Span);
                break;

            // --- operators ---
            case UnaryExpr n:
                Line(sb, indent, $"Unary {n.Operator}", n.Span);
                Write(n.Operand, indent + 1, sb);
                break;
            case PostfixExpr n:
                Line(sb, indent, $"Postfix {n.Operator}", n.Span);
                Write(n.Operand, indent + 1, sb);
                break;
            case BinaryExpr n:
                Line(sb, indent, $"Binary {n.Operator}", n.Span);
                Write(n.Left, indent + 1, sb);
                Write(n.Right, indent + 1, sb);
                break;
            case AssignExpr n:
                Line(sb, indent, $"Assign {(n.Operator is null ? "=" : $"{n.Operator}=")}", n.Span);
                Write(n.Target, indent + 1, sb);
                Write(n.Value, indent + 1, sb);
                break;
            case RangeExpr n:
                Line(sb, indent, $"Range {(n.IsInclusive ? "..=" : "..")}", n.Span);
                Write(n.Low, indent + 1, sb);
                Write(n.High, indent + 1, sb);
                break;
            case CastExpr n:
                Line(sb, indent, "Cast", n.Span);
                Write(n.Operand, indent + 1, sb);
                Write(n.Type, indent + 1, sb);
                break;

            // --- nodes produced by postfix ---
            case CallExpr n:
                Line(sb, indent, "Call", n.Span);
                Write(n.Callee, indent + 1, sb);
                foreach (var a in n.Arguments) Write(a, indent + 1, sb);
                break;
            case IndexExpr n:
                Line(sb, indent, "Index", n.Span);
                Write(n.Target, indent + 1, sb);
                Write(n.Index, indent + 1, sb);
                break;
            case MemberExpr n:
                Line(sb, indent, $"Member {n.Member}{(n.IsOptional ? " (optional)" : "")}", n.Span);
                Write(n.Target, indent + 1, sb);
                break;

            // --- composite literals ---
            case ArrayLitExpr n:
                Line(sb, indent, "Array", n.Span);
                foreach (var e in n.Elements) Write(e, indent + 1, sb);
                break;
            case TupleLitExpr n:
                Line(sb, indent, "Tuple", n.Span);
                foreach (var e in n.Elements) Write(e, indent + 1, sb);
                break;

            // --- f-strings ---
            case InterpolatedStringExpr n:
                Line(sb, indent, "FString", n.Span);
                foreach (var s in n.Segments) Write(s, indent + 1, sb);
                break;
            case InterpText n:
                Line(sb, indent, $"Text {Quote(n.Text)}", n.Span);
                break;
            case InterpHole n:
                Line(sb, indent, $"Hole{(n.FormatSpec is null ? "" : $" :{n.FormatSpec}")}", n.Span);
                Write(n.Expr, indent + 1, sb);
                break;

            // --- lambdas ---
            case LambdaExpr n:
                Line(sb, indent, "Lambda", n.Span);
                foreach (var p in n.Parameters) Write(p, indent + 1, sb);
                if (n.ReturnType is not null) Write(n.ReturnType, indent + 1, sb);
                Write(n.Body, indent + 1, sb);
                break;
            case LambdaParam n:
                Line(sb, indent, $"Param {n.Name}", n.Span);
                if (n.Type is not null) Write(n.Type, indent + 1, sb);
                break;

            // --- types ---
            case NullableType n:
                Line(sb, indent, "Nullable", n.Span);
                Write(n.Inner, indent + 1, sb);
                break;
            case NamedType n:
                Line(sb, indent, $"NamedType {string.Join('.', n.Path)}", n.Span);
                foreach (var a in n.TypeArguments) Write(a, indent + 1, sb);
                break;
            case ArrayType n:
                Line(sb, indent, $"ArrayType{(n.Size is null ? "" : $" [{n.Size.Value}]")}", n.Span);
                Write(n.Element, indent + 1, sb);
                break;
            case TupleType n:
                Line(sb, indent, "TupleType", n.Span);
                foreach (var e in n.Elements) Write(e, indent + 1, sb);
                break;
            case FunctionType n:
                Line(sb, indent, "FunctionType", n.Span);
                foreach (var p in n.Parameters) Write(p, indent + 1, sb);
                Write(n.ReturnType, indent + 1, sb);
                break;
            case ErrorType n:
                Line(sb, indent, "ErrorType", n.Span);
                break;

            // --- declarations ---
            case Module n:
                Line(sb, indent, n.Header is null ? "Module" : $"Module {string.Join('.', n.Header.Segments)}", n.Span);
                foreach (var d in n.Declarations) Write(d, indent + 1, sb);
                break;
            case ImportDecl n:
                Line(sb, indent, $"Import {string.Join('.', n.Path)}", n.Span);
                if (n.Clause is not null) Write(n.Clause, indent + 1, sb);
                break;
            case ImportSelective n:
                Line(sb, indent, $"Selective {string.Join(", ", n.Names)}", n.Span);
                break;
            case ImportAlias n:
                Line(sb, indent, $"Alias {n.Alias}", n.Span);
                break;
            case GenericParam n:
                Line(sb, indent, $"Generic {n.Name}", n.Span);
                foreach (var c in n.Constraints) Write(c, indent + 1, sb);
                break;
            case Param n:
                Line(sb, indent, $"Param {n.Name}{(n.IsParams ? " (params)" : "")}", n.Span);
                Write(n.Type, indent + 1, sb);
                if (n.Default is not null) Write(n.Default, indent + 1, sb);
                break;
            case ThrowsClause n:
                Line(sb, indent, n.Type is null ? "Throws (any)" : "Throws", n.Span);
                if (n.Type is not null) Write(n.Type, indent + 1, sb);
                break;
            case FunctionDecl n:
                Line(sb, indent, $"Fn {n.Name}{Vis(n.IsPublic)}{(n.IsMut ? " mut" : "")}{(n.Body is null ? " (abstract)" : "")}", n.Span);
                foreach (var g in n.Generics) Write(g, indent + 1, sb);
                foreach (var p in n.Parameters) Write(p, indent + 1, sb);
                if (n.ReturnType is not null) Write(n.ReturnType, indent + 1, sb);
                if (n.Throws is not null) Write(n.Throws, indent + 1, sb);
                if (n.Body is not null) Write(n.Body, indent + 1, sb);
                break;
            case FieldDecl n:
                Line(sb, indent, $"Field {n.Name}", n.Span);
                Write(n.Type, indent + 1, sb);
                if (n.Default is not null) Write(n.Default, indent + 1, sb);
                break;
            case StructDecl n:
                Line(sb, indent, $"Struct {n.Name}{Vis(n.IsPublic)}", n.Span);
                WriteTypeDeclChildren(n.Generics, n.Interfaces, n.Members, indent, sb);
                break;
            case ClassDecl n:
                Line(sb, indent, $"Class {n.Name}{Vis(n.IsPublic)}", n.Span);
                WriteTypeDeclChildren(n.Generics, n.Interfaces, n.Members, indent, sb);
                break;
            case EnumDecl n:
                Line(sb, indent, $"Enum {n.Name}{Vis(n.IsPublic)}", n.Span);
                foreach (var g in n.Generics) Write(g, indent + 1, sb);
                foreach (var i in n.Interfaces) Write(i, indent + 1, sb);
                foreach (var v in n.Variants) Write(v, indent + 1, sb);
                foreach (var m in n.Methods) Write(m, indent + 1, sb);
                break;
            case EnumVariant n:
                Line(sb, indent, $"Variant {n.Name}", n.Span);
                foreach (var t in n.TupleFields ?? []) Write(t, indent + 1, sb);
                foreach (var f in n.StructFields ?? []) Write(f, indent + 1, sb);
                break;
            case InterfaceDecl n:
                Line(sb, indent, $"Interface {n.Name}{Vis(n.IsPublic)}", n.Span);
                foreach (var g in n.Generics) Write(g, indent + 1, sb);
                foreach (var m in n.Members) Write(m, indent + 1, sb);
                break;
            case ExtendDecl n:
                Line(sb, indent, $"Extend{Vis(n.IsPublic)}", n.Span);
                Write(n.Target, indent + 1, sb);            // the first child is the target type
                foreach (var i in n.Interfaces) Write(i, indent + 1, sb);
                foreach (var m in n.Methods) Write(m, indent + 1, sb);
                break;
            case GlobalBindingDecl n:
                Line(sb, indent, $"Global{Vis(n.IsPublic)}", n.Span);
                Write(n.Binding, indent + 1, sb);
                break;
            case TypeAliasDecl n:
                Line(sb, indent, $"TypeAlias {n.Name}{Vis(n.IsPublic)}", n.Span);
                Write(n.Aliased, indent + 1, sb);
                break;
            case ErrorDecl n:
                Line(sb, indent, "ErrorDecl", n.Span);
                break;

            // A type-bound constant. It stands in the body of a type but contains an ordinary
            // BindingStmt, hence here rather than with the statements.
            case StaticBindingDecl n:
                Line(sb, indent, $"StaticLet{(n.IsPublic ? " pub" : "")}", n.Span);
                Write(n.Binding, indent + 1, sb);
                break;

            // --- statements ---
            case Block n:
                Line(sb, indent, "Block", n.Span);
                foreach (var s in n.Statements) Write(s, indent + 1, sb);
                break;
            case BindingStmt n:
                Line(sb, indent, $"{(n.IsMutable ? "Var" : "Let")} {n.Name}", n.Span);
                if (n.Type is not null) Write(n.Type, indent + 1, sb);
                if (n.Initializer is not null) Write(n.Initializer, indent + 1, sb);
                break;
            case DestructuringStmt n:
                Line(sb, indent, n.IsMutable ? "DestructureVar" : "DestructureLet", n.Span);
                Write(n.Pattern, indent + 1, sb);
                if (n.Type is not null) Write(n.Type, indent + 1, sb);
                Write(n.Initializer, indent + 1, sb);
                break;
            case IfStmt n:
                Line(sb, indent, "If", n.Span);
                Write(n.Condition, indent + 1, sb);
                Write(n.Then, indent + 1, sb);
                if (n.Else is not null) Write(n.Else, indent + 1, sb);
                break;
            case WhileStmt n:
                Line(sb, indent, "While", n.Span);
                Write(n.Condition, indent + 1, sb);
                Write(n.Body, indent + 1, sb);
                break;
            case DoWhileStmt n:
                Line(sb, indent, "DoWhile", n.Span);
                Write(n.Body, indent + 1, sb);
                Write(n.Condition, indent + 1, sb);
                break;
            case ForInStmt n:
                Line(sb, indent, $"ForIn {n.Variable}", n.Span);
                Write(n.Iterable, indent + 1, sb);
                Write(n.Body, indent + 1, sb);
                break;
            case BreakStmt n:
                Line(sb, indent, "Break", n.Span);
                break;
            case ContinueStmt n:
                Line(sb, indent, "Continue", n.Span);
                break;
            case ReturnStmt n:
                Line(sb, indent, "Return", n.Span);
                if (n.Value is not null) Write(n.Value, indent + 1, sb);
                break;
            case YieldStmt n:
                Line(sb, indent, "Yield", n.Span);
                if (n.Value is not null) Write(n.Value, indent + 1, sb);
                break;
            case ResumeExpr n:
                Line(sb, indent, "Resume", n.Span);
                Write(n.Coroutine, indent + 1, sb);
                break;
            case DeferStmt n:
                Line(sb, indent, "Defer", n.Span);
                Write(n.Body, indent + 1, sb);
                break;
            case ThrowStmt n:
                Line(sb, indent, "Throw", n.Span);
                Write(n.Value, indent + 1, sb);
                break;
            case TryStmt n:
                Line(sb, indent, "Try", n.Span);
                Write(n.Body, indent + 1, sb);
                foreach (var c in n.Catches) Write(c, indent + 1, sb);
                break;
            case CatchClause n:
                Line(sb, indent, $"Catch {n.BindingName ?? "_"}", n.Span);
                if (n.BindingType is not null) Write(n.BindingType, indent + 1, sb);
                Write(n.Body, indent + 1, sb);
                break;
            case ExprStmt n:
                Line(sb, indent, "ExprStmt", n.Span);
                Write(n.Expr, indent + 1, sb);
                break;
            case ErrorStmt n:
                Line(sb, indent, "ErrorStmt", n.Span);
                break;

            // --- control flow as an expression ---
            case IfExpr n:
                Line(sb, indent, "IfExpr", n.Span);
                Write(n.Condition, indent + 1, sb);
                Write(n.Then, indent + 1, sb);
                Write(n.Else, indent + 1, sb);
                break;
            case MatchExpr n:
                Line(sb, indent, "Match", n.Span);
                Write(n.Scrutinee, indent + 1, sb);
                foreach (var a in n.Arms) Write(a, indent + 1, sb);
                break;
            case MatchStmt n:
                Line(sb, indent, "MatchStmt", n.Span);
                Write(n.Scrutinee, indent + 1, sb);
                foreach (var a in n.Arms) Write(a, indent + 1, sb);
                break;
            case MatchArm n:
                Line(sb, indent, "Arm", n.Span);
                Write(n.Pattern, indent + 1, sb);
                if (n.Guard is not null)
                {
                    Line(sb, indent + 1, "Guard", n.Guard.Span);
                    Write(n.Guard, indent + 2, sb);
                }
                Write(n.Body, indent + 1, sb); // the last child is the arm body
                break;

            // --- patterns ---
            case WildcardPattern n:
                Line(sb, indent, "Wildcard", n.Span);
                break;
            case LiteralPattern n:
                Line(sb, indent, "LitPattern", n.Span);
                Write(n.Literal, indent + 1, sb);
                break;
            case BindingPattern n:
                Line(sb, indent, $"BindPattern {n.Name}", n.Span);
                break;
            case VariantPattern n:
                Line(sb, indent, $"VariantPattern {string.Join('.', n.Path)}", n.Span);
                foreach (var p in n.TupleElements ?? []) Write(p, indent + 1, sb);
                foreach (var f in n.StructFields ?? []) Write(f, indent + 1, sb);
                break;
            case TuplePattern n:
                Line(sb, indent, "TuplePattern", n.Span);
                foreach (var p in n.Elements) Write(p, indent + 1, sb);
                break;
            case RangePattern n:
                Line(sb, indent, $"RangePattern {(n.IsInclusive ? "..=" : "..")}", n.Span);
                Write(n.Low, indent + 1, sb);
                Write(n.High, indent + 1, sb);
                break;
            case OrPattern n:
                Line(sb, indent, "OrPattern", n.Span);
                foreach (var p in n.Alternatives) Write(p, indent + 1, sb);
                break;
            case FieldPattern n:
                Line(sb, indent, $"FieldPattern {n.Name}", n.Span);
                if (n.Pattern is not null) Write(n.Pattern, indent + 1, sb);
                break;
            case ErrorPattern n:
                Line(sb, indent, "ErrorPattern", n.Span);
                break;

            // --- struct initializers ---
            case StructInitExpr n:
                Line(sb, indent, $"StructInit {string.Join('.', n.Path)}", n.Span);
                foreach (var a in n.TypeArguments) { Line(sb, indent + 1, "TypeArg", a.Span); Write(a, indent + 2, sb); }
                foreach (var f in n.Fields) Write(f, indent + 1, sb);
                break;
            case StructInitField n:
                Line(sb, indent, $"InitField {n.Name}", n.Span);
                Write(n.Value, indent + 1, sb);
                break;

            // --- a type path in value position: Pair<int>.of(3) ---
            case TypePathExpr n:
                Line(sb, indent, $"TypePath {string.Join('.', n.Path)}", n.Span);
                foreach (var a in n.TypeArguments) { Line(sb, indent + 1, "TypeArg", a.Span); Write(a, indent + 2, sb); }
                break;

            // --- recovery ---
            case ErrorExpr n:
                Line(sb, indent, "Error", n.Span);
                break;

            default:
                throw new InternalCompilationException($"AstDumper: unhandled node {node.GetType().Name}");
        }
    }

    // struct and class share their child order: generics, interfaces, members.
    private static void WriteTypeDeclChildren(GenericParam[] generics, TypeNode[] interfaces, Decl[] members,
        int indent, StringBuilder sb)
    {
        foreach (var g in generics) Write(g, indent + 1, sb);
        foreach (var i in interfaces) Write(i, indent + 1, sb);
        foreach (var m in members) Write(m, indent + 1, sb);
    }

    private static string Vis(bool isPublic) => isPublic ? " pub" : "";

    private static void Line(StringBuilder sb, int indent, string text, Span span)
    {
        sb.Append(' ', indent * 2);
        sb.Append(text);
        sb.Append(' ');
        sb.Append('[').Append(span.Start).Append("..").Append(span.End).Append(')');
        sb.Append('\n');
    }

    private static string Suffix(IntSuffix? s) => s is null ? "" : $" {s}";
    private static string Suffix(FloatSuffix? s) => s is null ? "" : $" {s}";

    private static string Quote(string s)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
