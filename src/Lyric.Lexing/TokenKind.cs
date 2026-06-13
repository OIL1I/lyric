namespace Lyric.Lexing;

public enum TokenKind
{
    Eof,
    BadChar,
    Identifier,
    AtIdentifier,
    
    // Braces
    LParen,
    RParen,
    LBrace,
    RBrace,
    LBracket,
    RBracket,

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

    // Operators
    //Punctuation
    Comma,
    Dot,
    Semicolon,
    Colon,
    ColonColon,
    Arrow,
    FatArrow,
    
    //Optional/Nullable
    Question,
    QuestionDot,
    QuestionQuestion,
    Exclamation, //! ist prefix(logical not) und postfix(unwrap), parser disambiguiert
    
    //Arithmetic
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Inc, //++
    Dec, //--
    
    //Bitwise
    Amp, //&
    Pipe, //|
    Caret, //^
    Tilde, //~
    Shl, //<<
    Shr, // >>
    
    //Comparison
    EqualEqual, // ==
    ExclamationEqual, // !=
    Less, // <
    LessEqual, // <=
    Greater, // >
    GreaterEqual, // >=
    
    //Logical
    AmpAmp, // &&
    PipePipe, // ||
    
    //Range 
    DotDot, //..
    DotDotEqual, //..= 
    
    //Assignment
    Equal, // =
    PlusEqual, // +=
    MinusEqual, // -=
    StarEqual, // *=
    SlashEqual, // /=
    PercentEqual, // %=
    AmpEqual, // &=
    PipeEqual, // |=
    CaretEqual, // ^=
    ShlEqual, // <<=
    ShrEqual, // >>=
    AmpAmpEqual, // &&=
    PipePipeEqual, // ||=
    QuestionQuestionEqual, // ??=
    
    // This
    This,

    // Doc comments
    DocComment
}