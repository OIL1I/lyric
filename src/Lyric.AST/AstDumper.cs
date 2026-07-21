using System.Globalization;
using System.Text;
using Lyric.Core;

namespace Lyric.AST;

/// <summary>
/// Deterministischer Baum-Dump eines AST-Knotens für Golden-Snapshots und
/// <c>lyric parse</c>. Ein Knoten pro Zeile, 2 Spaces Einrückung pro Ebene,
/// Span als <c>[start..end)</c> am Zeilenende (analog zu <see cref="Lexing"/>s
/// TokenDumper).
///
/// Kind-Reihenfolge ist fix und positionsbasiert (keine Rollen-Labels):
///   Binary/Assign/Range : Left, Right
///   Cast                : Operand, Type
///   Call                : Callee, dann Argumente
///   Index               : Target, Index
///   Member/Unary/Postfix: Operand
///   Hole                : Expr
///   Lambda              : Parameter*, [ReturnType], Body   (Body immer letztes Kind)
///   FunctionType        : Parameter*, ReturnType           (ReturnType immer letztes Kind)
///
/// Der Dumper ist bewusst über <c>switch</c> statt Visitor gelöst: alle Fälle an
/// einer Stelle, der <c>default</c>-Wurf erzwingt Vollständigkeit bei neuen Knoten.
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
            // --- Literale ---
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

            // --- Namen ---
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

            // --- Operatoren ---
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

            // --- Postfix-erzeugte Knoten ---
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

            // --- Zusammengesetzte Literale ---
            case ArrayLitExpr n:
                Line(sb, indent, "Array", n.Span);
                foreach (var e in n.Elements) Write(e, indent + 1, sb);
                break;
            case TupleLitExpr n:
                Line(sb, indent, "Tuple", n.Span);
                foreach (var e in n.Elements) Write(e, indent + 1, sb);
                break;

            // --- f-Strings ---
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

            // --- Lambdas ---
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

            // --- Typen ---
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

            // --- Recovery ---
            case ErrorExpr n:
                Line(sb, indent, "Error", n.Span);
                break;

            default:
                throw new InternalCompilationException($"AstDumper: unhandled node {node.GetType().Name}");
        }
    }

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
