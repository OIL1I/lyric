# Lyric Grammar

The formal grammar of the Lyric language. This document defines the syntax only; it says nothing
about typing, name resolution or runtime behaviour.

## Notation

EBNF with the following conventions:

| Form | Meaning |
|---|---|
| `'x'` | terminal |
| `X` | non-terminal |
| `{ X }` | zero or more |
| `[ X ]` | zero or one |
| `X \| Y` | alternative |
| `( X )` | grouping |
| `X .` | end of production |
| `(* … *)` | note |

`a'..'z` denotes a character range. `any-char` denotes any character of the source encoding.

---

## 1. Lexical grammar

### 1.1 Source file

```ebnf
SourceFile      = { Trivia | Token } EOF .
Trivia          = Whitespace | LineComment | BlockComment .
```

Source is UTF-8. A byte-order mark at offset 0 is skipped. Line terminators are `\n` and `\r\n`.

### 1.2 Whitespace and comments

```ebnf
Whitespace      = ' ' | '\t' | '\n' | '\r' .
LineComment     = '//' { any-char-except-newline } .
BlockComment    = '/*' { BlockComment | any-char } '*/' .
DocComment      = '///' { any-char-except-newline } .
```

Block comments nest: `/* /* */ */` is one comment. Doc comments are tokenized and carry no
grammatical meaning.

### 1.3 Identifiers

```ebnf
IdentStart      = 'a'..'z' | 'A'..'Z' | '_' .
IdentCont       = IdentStart | '0'..'9' .
IDENTIFIER      = IdentStart { IdentCont } .        (* unless it is a keyword *)
AT_IDENT        = '@' IdentStart { IdentCont } .
```

### 1.4 Keywords

Reserved; never an identifier:

```text
module    import    as        pub       static
struct    class     enum      interface extend
fn        mut       let       var       params
if        else      while     do        for       in    match
break     continue  return    yield     resume    defer
try       catch     throw
true      false     null
this
```

Contextual; reserved only in the position shown, an identifier everywhere else:

| Word | Position |
|---|---|
| `type` | first token of a top-level declaration, before an identifier |
| `throws` | after the return type of a function signature |

Built-in type names (`int`, `string`, …) are identifiers, not keywords.

### 1.5 Literals

```ebnf
IntLit          = ( DecLit | HexLit | BinLit | OctLit ) [ IntSuffix ] .
DecLit          = DecDigit { DecDigit | '_' } .
HexLit          = '0' ( 'x' | 'X' ) HexDigit { HexDigit | '_' } .
BinLit          = '0' ( 'b' | 'B' ) BinDigit { BinDigit | '_' } .
OctLit          = '0' ( 'o' | 'O' ) OctDigit { OctDigit | '_' } .
IntSuffix       = ( 'i' | 'u' ) DecDigit { DecDigit } .   (* i8 … i64, u8 … u64 *)

FloatLit        = DecLit ( '.' DecLit [ Exponent ] | Exponent ) [ FloatSuffix ]
                | DecLit FloatSuffix .
Exponent        = ( 'e' | 'E' ) [ '+' | '-' ] DecDigit { DecDigit | '_' } .
FloatSuffix     = 'f' DecDigit { DecDigit } .             (* f32, f64 *)

StringLit       = '"' { StringChar | EscapeSeq } '"' .
InterpolatedStr = 'f' '"' { StringChar | EscapeSeq | Interpolation } '"' .
Interpolation   = '{' Expr [ ':' FormatSpec ] '}' .
CharLit         = "'" ( CharChar | EscapeSeq ) "'" .
EscapeSeq       = '\' ( 'n' | 'r' | 't' | '\' | '"' | "'" | '0'
                      | 'x' HexDigit HexDigit
                      | 'u' '{' HexDigit { HexDigit } '}' ) .

BoolLit         = 'true' | 'false' .
NullLit         = 'null' .
```

Inside an interpolated string, `{{` and `}}` produce a literal brace. `FormatSpec` runs to the
matching `}`, tracking nested braces, parentheses and brackets.

### 1.6 Operators and punctuation

```text
(   )   {   }   [   ]
,   .   ;   :   ::  ->  =>
?   ?.  ??  !
+   -   *   /   %
&   |   ^   ~
<<  >>
==  !=  <   <=  >   >=
&&  ||  !
++  --
..  ..=
=   +=  -=  *=  /=  %=
&=  |=  ^=  <<= >>=
&&= ||= ??=
```

The lexer takes the longest match: `<<=` before `<<` before `<`.

`::` introduces an interface list and never appears in a module path; `.` separates path
segments. `!` is postfix force-unwrap and prefix logical not.

---

## 2. Module structure

```ebnf
Module          = ModuleHeader { TopLevelDecl } .
ModuleHeader    = 'module' ModulePath ';' .
ModulePath      = IDENTIFIER { '.' IDENTIFIER } .

ImportDecl      = 'import' ModulePath [ ImportClause ] ';' .
ImportClause    = '{' IDENTIFIER { ',' IDENTIFIER } [ ',' ] '}'
                | 'as' IDENTIFIER .

TopLevelDecl    = ImportDecl
                | [ 'pub' ] ( FunctionDecl
                            | StructDecl
                            | ClassDecl
                            | EnumDecl
                            | InterfaceDecl
                            | ExtendDecl
                            | GlobalBinding
                            | TypeAlias ) .

GlobalBinding   = BindingStmt .                   (* 'let' only *)
TypeAlias       = 'type' IDENTIFIER '=' TypeExpr ';' .
```

The module header is optional in a file compiled as an entry point; the module name then comes
from the file name.

---

## 3. Declarations

### 3.1 Functions

```ebnf
FunctionDecl    = [ 'pub' ] [ 'static' ] [ 'mut' ] 'fn' IDENTIFIER [ GenericParams ]
                  '(' [ ParamList ] ')' [ ':' TypeExpr ]
                  [ 'throws' [ TypeExpr ] ]
                  ( Block | ';' ) .
GenericParams   = '<' GenericParam { ',' GenericParam } '>' .
GenericParam    = IDENTIFIER [ '::' '[' TypeExpr { ',' TypeExpr } ']' ] .

ParamList       = Param { ',' Param } .
Param           = [ 'params' ] IDENTIFIER ':' TypeExpr [ '=' Expr ] .
```

A body of `;` declares a function without one. `params` may appear on the last parameter only,
whose type must be an array.

### 3.2 Structs

```ebnf
StructDecl      = [ 'pub' ] 'struct' IDENTIFIER [ GenericParams ]
                  [ '::' InterfaceList ]
                  '{' [ StructBody ] '}' .
InterfaceList   = '[' TypeExpr { ',' TypeExpr } ']' .
StructBody      = { StructMember [ ',' ] } .
StructMember    = Field | FunctionDecl | StaticBinding .
StaticBinding   = [ 'pub' ] 'static' BindingStmt .
Field           = IDENTIFIER ':' TypeExpr [ '=' Expr ] .
```

Members are separated by `,`. A member that ends in a block (`}`) may omit it; a member that ends
in `;` requires it.

### 3.3 Classes

```ebnf
ClassDecl       = [ 'pub' ] 'class' IDENTIFIER [ GenericParams ]
                  [ '::' InterfaceList ]
                  '{' [ ClassBody ] '}' .
ClassBody       = { ClassMember [ ',' ] } .
ClassMember     = Field | FunctionDecl | StaticBinding .
```

### 3.4 Enums

```ebnf
EnumDecl        = [ 'pub' ] 'enum' IDENTIFIER [ GenericParams ]
                  [ '::' InterfaceList ]
                  '{' [ EnumBody ] '}' .
EnumBody        = EnumVariant { ',' EnumVariant } [ ',' ]
                  [ ';' { FunctionDecl } ] .
EnumVariant     = IDENTIFIER [ TupleVariant | StructVariant ] .
TupleVariant    = '(' TypeExpr { ',' TypeExpr } ')' .
StructVariant   = '{' Field { ',' Field } [ ',' ] '}' .
```

The `;` separates the variant list from the method list.

### 3.5 Interfaces

```ebnf
InterfaceDecl   = [ 'pub' ] 'interface' IDENTIFIER [ GenericParams ]
                  '{' { InterfaceMember } '}' .
InterfaceMember = FunctionDecl .
```

An interface member with a body is a default implementation. An interface declares no interface
list; there is no interface inheritance.

### 3.6 Extend blocks

```ebnf
ExtendDecl      = 'extend' TypeExpr [ '::' InterfaceList ]
                  '{' { FunctionDecl } '}' .
```

---

## 4. Type expressions

```ebnf
TypeExpr        = TypePrefix TypeAtom { TypeSuffix } .
TypePrefix      = [ '?' ] .
TypeAtom        = BuiltinType
                | ModulePath [ '<' TypeExpr { ',' TypeExpr } '>' ]
                | FunctionType
                | TupleType
                | GroupedType .
FunctionType    = 'fn' '(' [ TypeExpr { ',' TypeExpr } ] ')' '->' TypeExpr .
TupleType       = '(' TypeExpr ',' TypeExpr { ',' TypeExpr } ')' .   (* arity >= 2 *)
GroupedType     = '(' TypeExpr ')' .
TypeSuffix      = '[' ']' .

BuiltinType     = 'int' | 'uint' | 'float'
                | 'int8' | 'int16' | 'int32' | 'int64'
                | 'uint8' | 'uint16' | 'uint32' | 'uint64'
                | 'float32' | 'float64'
                | 'bool' | 'char' | 'string' | 'void' .
```

A function type extends as far to the right as possible: `fn(int) -> void[]` is a function
returning `void[]`. An array of function values is written `(fn(int) -> void)[]`.

`?` binds to the atom together with its suffixes: `?T[]` is an optional array. An array of
optionals is written `(?T)[]`.

`?` does not nest: `??T` is not a type.

---

## 5. Statements

```ebnf
Block           = '{' { Statement } '}' .

Statement       = Block
                | BindingStmt
                | DestructuringStmt
                | IfStmt
                | WhileStmt
                | DoWhileStmt
                | ForInStmt
                | MatchStmt
                | BreakStmt
                | ContinueStmt
                | ReturnStmt
                | YieldStmt
                | DeferStmt
                | ThrowStmt
                | TryStmt
                | ExprStmt .

BindingStmt     = ( 'let' | 'var' ) IDENTIFIER [ ':' TypeExpr ] [ '=' Expr ] ';' .
DestructuringStmt = ( 'let' | 'var' ) TuplePattern [ ':' TypeExpr ] '=' Expr ';' .

IfStmt          = 'if' '(' Expr ')' Block [ 'else' ( Block | IfStmt ) ] .

WhileStmt       = 'while' '(' Expr ')' Block .
DoWhileStmt     = 'do' Block 'while' '(' Expr ')' ';' .
ForInStmt       = 'for' '(' IDENTIFIER 'in' Expr ')' Block .

MatchStmt       = 'match' '(' Expr ')' '{' { MatchArm } '}' .
MatchArm        = Pattern [ 'if' Expr ] '=>' ( Expr | Block ) .

BreakStmt       = 'break' ';' .
ContinueStmt    = 'continue' ';' .
ReturnStmt      = 'return' [ Expr ] ';' .
YieldStmt       = 'yield' [ Expr ] ';' .
DeferStmt       = 'defer' ( Block | Expr ';' ) .
ThrowStmt       = 'throw' Expr ';' .

TryStmt         = 'try' Block { CatchClause } .
CatchClause     = 'catch' '(' CatchBinding ')' Block .
CatchBinding    = '_'
                | IDENTIFIER ':' TypeExpr
                | IDENTIFIER .

ExprStmt        = Expr ';' .
```

A destructuring binding requires an initializer. Its pattern admits names, `_` and nested tuple
patterns; no form that can fail.

`ExprStmt` admits a call, an assignment or `resume`. A statement does not begin with a struct
initializer: at that position `Name { … }` is a name followed by a block.

An arm whose body is an expression is followed by `,`, except for the last arm before `}`. An arm
whose body is a block may omit it.

---

## 6. Expressions

### 6.1 Precedence

Highest first. All levels are left-associative unless stated.

| # | Operators | Associativity |
|---|---|---|
| 1 | postfix `.` `?.` `[ ]` `( )` `++` `--` `!` | left |
| 2 | prefix `!` `-` `~` `++` `--` `resume` | right |
| 3 | `as` | left |
| 4 | `*` `/` `%` | left |
| 5 | `+` `-` | left |
| 6 | `<<` `>>` | left |
| 7 | `..` `..=` | non-associative |
| 8 | `&` | left |
| 9 | `^` | left |
| 10 | `\|` | left |
| 11 | `<` `<=` `>` `>=` | left |
| 12 | `==` `!=` | left |
| 13 | `&&` | left |
| 14 | `\|\|` | left |
| 15 | `??` | right |
| 16 | assignment | right |

### 6.2 Grammar

```ebnf
Expr            = Assign .
Assign          = Coalesce [ AssignOp Assign ] .
AssignOp        = '=' | '+=' | '-=' | '*=' | '/=' | '%='
                | '&=' | '|=' | '^=' | '<<=' | '>>='
                | '&&=' | '||=' | '??=' .

Primary         = IntLit | FloatLit | StringLit | InterpolatedStr
                | CharLit | BoolLit | NullLit
                | 'this'
                | IDENTIFIER
                | TypePath
                | AT_IDENT [ '(' [ ArgList ] ')' ]
                | '(' Expr ')'
                | IfExpr
                | MatchExpr
                | StructInit
                | ArrayLit
                | TupleLit
                | Lambda .

Lambda          = '(' [ LambdaParam { ',' LambdaParam } ] ')' [ ':' TypeExpr ]
                  '=>' ( Expr | Block ) .
LambdaParam     = IDENTIFIER [ ':' TypeExpr ] .

ResumeExpr      = 'resume' UnaryExpr .

StructInit      = TypePath '{' [ StructInitField { ',' StructInitField } [ ',' ] ] '}' .
StructInitField = IDENTIFIER '=' Expr .

TypePath        = ModulePath [ '<' TypeExpr { ',' TypeExpr } '>' ] [ '.' IDENTIFIER ] .

CallArgs        = [ '<' TypeExpr { ',' TypeExpr } '>' ] '(' [ Expr { ',' Expr } ] ')' .

ArrayLit        = '[' [ Expr { ',' Expr } [ ',' ] ] ']' .
TupleLit        = '(' Expr ',' Expr { ',' Expr } ')' .

IfExpr          = 'if' '(' Expr ')' Expr 'else' Expr .
MatchExpr       = 'match' '(' Expr ')' '{' { MatchArm } '}' .
```

`else` is mandatory in `IfExpr`; `else if` is a nested `IfExpr`.

In a `TypePath`, the trailing `'.' IDENTIFIER` names an enum variant: `Opt<int>.Some`. The type
arguments belong to the type and precede that segment.

### 6.3 Resolving `<`

A `<` after a name opens a type-argument list when it closes balanced and only tokens that can
occur in a type expression stand between the two, and the token that follows is one of:

| Follower | Form |
|---|---|
| `(` | call with explicit type arguments — `f<int>()` |
| `{` | struct initializer — `Pair<int> { … }` |
| `.` | type path in value position — `Pair<int>.of(3)` |

In every other case `<` is the comparison operator.

---

## 7. Patterns

```ebnf
Pattern         = '_'
                | Literal
                | IDENTIFIER
                | TypePath [ '(' Pattern { ',' Pattern } ')' ]
                | TypePath '{' [ FieldPattern { ',' FieldPattern } [ ',' ] ] '}'
                | TuplePattern
                | Pattern '|' Pattern
                | RangePattern .

FieldPattern    = IDENTIFIER [ '=' Pattern ] .
TuplePattern    = '(' Pattern ',' Pattern { ',' Pattern } ')' .
RangePattern    = Literal ( '..' | '..=' ) Literal .
```

`Literal` is an integer, float, string, char, bool or null literal. A `FieldPattern` without `=`
binds the field to its own name.
