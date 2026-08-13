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

    /// <summary>A member without a receiver. Only allowed inside a struct or class body.</summary>
    Static,

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
    IntLiteral ,     // all bases: dec, hex, bin, oct, with or without an integer suffix
    FloatLiteral,    // decimal with a '.', with an exponent, or with a float suffix
    StringLiteral,
    CharLiteral,
    
    // FStrings
    FStringStart,       // f"
    FStringChunk,       // a plain-text span between specials
    FStringInterpStart, // { in f-String
    FStringInterpEnd,   // the } that closes an interpolation
    FStringFormatSpec,  // the span between ':' and '}'
    FStringEnd,         // the closing quote

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
    Exclamation, // prefix (logical not) and postfix (unwrap); the parser disambiguates
    
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
