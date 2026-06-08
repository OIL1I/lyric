namespace Lyric.Lexing;

public enum TokenKind
{
    Eof,
    BadChar,
    Identifier,
    LParen,
    RParen,
    LBrace,
    RBrace,

    // Module
    Module,
    Import,
    As,
    Pub,

    // Type declarations
    Struct,
    Class,
    Enum,
    Interface,
    Extend,

    // Function / binding
    Fn,
    Mut,
    Let,
    Var,
    Params,

    // Control flow
    If,
    Else,
    While,
    Do,
    For,
    In,
    Match,

    // Jumps
    Break,
    Continue,
    Return,
    Yield,
    Resume,
    Defer,

    // Exceptions
    Try,
    Catch,
    Throw,

    // Literals
    True,
    False,
    Null,

    // This
    This,

    // Doc comments
    DocComment
}