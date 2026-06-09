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
    IntLiteral ,     // alle Bases: dec, hex, bin, oct, mit/ohne Int-Suffix
    FloatLiteral,    // Dec mit '.' DecLit, oder Dec mit Exponent, oder Dec mit Float-Suffix
    StringLiteral,
    CharLiteral,
    
    // FStrings
    FStringStart,       // f"
    FStringChunk,       // Plain-Text-Span zwischen Specials
    FStringInterpStart, // { in f-String
    FStringInterpEnd,   // } die Interp schließt
    FStringFormatSpec,  // Span zwischen : und }
    FStringEnd,         // schließendes "

    // This
    This,

    // Doc comments
    DocComment
}