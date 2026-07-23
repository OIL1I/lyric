# Lyric – Formelle Sprachspezifikation v1

> Diese Datei beschreibt die Sprache **Lyric** (Compiler `lyric`, Datei-Endung `.lyr`) in EBNF-ähnlicher Notation. Sie ist der verbindliche Sprach-Vertrag für v1.
>
> Konvention: `'literal'` = wörtlich, `UPPER_CASE` = lexikalische Klasse, `( … )` = Gruppe, `[ … ]` = optional, `{ … }` = 0..n Wiederholungen, `|` = Alternative.
>
> Stand: v1.0-Draft. Bei Konflikt mit `Doku.md` oder `ROADMAP.md` gilt diese Datei.

---

## 1. Lexikalische Struktur

### 1.1 Quelldatei

```ebnf
SourceFile      = { Trivia | Token } EOF .
Trivia          = Whitespace | LineComment | BlockComment .
```

Encoding: UTF-8. Alle Schlüsselwörter sind ASCII. Identifier-Continuations sind ASCII. UTF-8 in String- und Char-Literalen erlaubt.

### 1.2 Whitespace und Kommentare

```ebnf
Whitespace      = ' ' | '\t' | '\n' | '\r' .
LineComment     = '//' { any-char-except-newline } .
BlockComment    = '/*' { BlockComment | any-char } '*/' .   (* verschachtelbar *)
DocComment      = '///' …                                    (* tokenisiert, Semantik post-v1 *)
```

### 1.3 Identifier

```ebnf
IdentStart      = 'a'..'z' | 'A'..'Z' | '_' .
IdentCont       = IdentStart | '0'..'9' .
IDENTIFIER      = IdentStart { IdentCont } .                 (* sofern kein Keyword *)
AT_IDENT        = '@' IdentStart { IdentCont } .             (* @-prefixed Attribute *)
```

### 1.3a Namens-Konvention (verbindlich, von Linter durchgesetzt — siehe ROADMAP.md ADR-005)

| Kategorie | Stil | Beispiel |
|---|---|---|
| Typen (struct/class/enum/interface) | `PascalCase` | `Player`, `HttpRequest` |
| Enum-Varianten | `PascalCase` | `Red`, `NotFound` |
| Funktionen, Methoden, Felder, Variablen, Parameter | `camelCase` | `playerName`, `takeDamage`, `maxHp` |
| Konstanten (Compile-Time-Literale auf Top-Level) | `camelCase` oder `SCREAMING_SNAKE` | `pi`, `MAX_BUFFER` |
| Module | `lowercase` (dot-getrennt) | `std.io`, `game.entities` |

### 1.4 Schlüsselwörter

```text
module    import    as        pub
struct    class     enum      interface  extend
fn        mut       let       var        params
if        else      while     do         for    in    match
break     continue  return    yield      resume   defer
try       catch     throw
true      false     null
this
```

**Reserviert für post-v1** (in v1 sind das normale Identifier, dürfen also als Variablen-/Funktions-Namen verwendet werden):
```text
async   await   const   trait   move   own
```

### 1.5 Literale

```ebnf
IntLit          = ( DecLit | HexLit | BinLit | OctLit ) [ IntSuffix ] .
DecLit          = DecDigit { DecDigit | '_' } .
HexLit          = '0' ( 'x' | 'X' ) HexDigit { HexDigit | '_' } .
BinLit          = '0' ( 'b' | 'B' ) BinDigit { BinDigit | '_' } .
OctLit          = '0' ( 'o' | 'O' ) OctDigit { OctDigit | '_' } .
IntSuffix       = ( 'i' | 'u' ) DecDigit { DecDigit } .       (* i8, i16, i32, i64, u8, ..., u64 *)

FloatLit        = DecLit ( '.' DecLit [ Exponent ] | Exponent ) [ FloatSuffix ]
                | DecLit FloatSuffix .
Exponent        = ( 'e' | 'E' ) [ '+' | '-' ] DecDigit { DecDigit | '_' } .
FloatSuffix     = 'f' DecDigit { DecDigit } .                  (* f32, f64 *)

StringLit       = '"' { StringChar | EscapeSeq } '"' .
InterpolatedStr = 'f' '"' { StringChar | EscapeSeq | Interpolation } '"' .
Interpolation   = '{' Expr [ ':' FormatSpec ] '}' .
CharLit         = '\'' ( CharChar | EscapeSeq ) '\'' .
EscapeSeq       = '\\' ( 'n' | 'r' | 't' | '\\' | '"' | '\'' | '0'
                       | 'x' HexDigit HexDigit
                       | 'u' '{' HexDigit { HexDigit } '}' ) .

BoolLit         = 'true' | 'false' .
NullLit         = 'null' .
```

String-Interpolation `f"hello {name}"` ist v1-Pflicht (anders als bei Oil). Format-Spec analog zu .NET (`{value:N2}`, `{value:0>5}`).

### 1.6 Operatoren und Interpunktion

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

Lexer arbeitet mit **Longest-Match** (`<<=` schlägt `<<`, das schlägt `<`).

**Spezialfälle:**
- `::` ist der „implements"-Operator: `struct X :: [I1, I2]`
- `::` taucht **nicht** in Modul-Pfaden auf (das ist `.`)
- `!` ist Postfix-Force-Unwrap (`expr!`) und Prefix-Logical-Not (`!expr`)

---

## 2. Modulebene

### 2.1 Modul-Identität

Eine Datei = ein Modul. Modulname wird aus dem Dateipfad relativ zum Source-Root abgeleitet:

- `src/main.lyr` → Modul `main`
- `src/game/player.lyr` → Modul `game.player`
- `src/game/entities/enemy.lyr` → Modul `game.entities.enemy`

Optional darf eine Datei einen expliziten Header haben:

```ebnf
ModuleHeader    = 'module' ModulePath ';' .
ModulePath      = IDENTIFIER { '.' IDENTIFIER } .
```

Wenn vorhanden, muss der Header zum inferierten Pfad konsistent sein (`LYR-RES0001`).

### 2.2 Imports

```ebnf
ImportDecl      = 'import' ModulePath [ ImportClause ] ';' .
ImportClause    = '{' IDENTIFIER { ',' IDENTIFIER } [ ',' ] '}'    (* selektiv *)
                | 'as' IDENTIFIER .                                  (* alias *)
```

Drei Formen:

```lyr
import std.io;                              // Namespace-Import: io.println(...)
import std.io { println, eprintln };        // selektiv: println(...)
import std.collections.HashMap as Dict;     // alias: Dict<string, int>()
```

Wildcard-Imports (`import std.io.*`) sind **nicht** unterstützt (`LYR-RES0002`).

### 2.3 Top-Level-Deklarationen

```ebnf
Module          = ModuleHeader { TopLevelDecl } .
TopLevelDecl    = ImportDecl
                | [ 'pub' ] ( FunctionDecl
                            | StructDecl
                            | ClassDecl
                            | EnumDecl
                            | InterfaceDecl
                            | ExtendDecl
                            | GlobalBinding
                            | TypeAlias ) .

GlobalBinding   = BindingStmt .                                      (* nur let, nicht var *)
TypeAlias       = 'type' IDENTIFIER '=' TypeExpr ';' .
```

Sichtbarkeit: `pub` exportiert, Default = modul-privat.

---

## 3. Deklarationen

### 3.1 Funktionen

```ebnf
FunctionDecl    = [ 'pub' ] [ 'mut' ] 'fn' IDENTIFIER [ GenericParams ]
                  '(' [ ParamList ] ')' [ ':' TypeExpr ]
                  [ 'throws' [ TypeExpr ] ]
                  ( Block | ';' ) .
GenericParams   = '<' GenericParam { ',' GenericParam } '>' .
GenericParam    = IDENTIFIER [ '::' '[' TypeExpr { ',' TypeExpr } ']' ] .

ParamList       = Param { ',' Param } .
Param           = [ 'params' ] IDENTIFIER ':' TypeExpr [ '=' Expr ] .
```

Regeln (Sema):

- `mut` ist nur als Methoden-Marker erlaubt (Struct-Receiver-Mutation). Free `fn` darf kein `mut` haben.
- `params` ist nur am **letzten** Parameter erlaubt und erfordert einen Array-Typ.
- Default-Werte sind nur an Trailing-Parametern erlaubt.
- `throws` ohne Typ: kann jeden `Throwable` werfen. `throws SomeError`: wirft nur diesen Typ (oder Subtypen).
- Interface-Methoden mit Body sind Defaults; ohne Body abstrakt.

### 3.2 Structs (Value-Typ)

```ebnf
StructDecl      = [ 'pub' ] 'struct' IDENTIFIER [ GenericParams ]
                  [ '::' InterfaceList ]
                  '{' [ StructBody ] '}' .
InterfaceList   = '[' TypeExpr { ',' TypeExpr } ']' .
StructBody      = { StructMember [ ',' ] } .                (* Trenner-Regel siehe unten *)
StructMember    = Field | FunctionDecl .
Field           = IDENTIFIER ':' TypeExpr [ '=' Expr ] .
```

**Member-Trenner-Regel** (gilt auch für `ClassBody`): Das `,` trennt Member. Nach
einem `Field` ist es **Pflicht**, außer das Feld ist das letzte Member vor `}`. Nach
einer `FunctionDecl` (Block-Body endet mit `}`) ist es **optional**. So brauchen
Methoden kein Trailing-Komma, Felder bleiben aber klar getrennt.

Beispiel:

```lyr
pub struct Vector3 :: [Equatable] {
    x: float,
    y: float,
    z: float,

    fn length(): float {
        return sqrt(this.x*this.x + this.y*this.y + this.z*this.z);
    }

    fn equals(other: Vector3): bool {
        return this.x == other.x && this.y == other.y && this.z == other.z;
    }
}
```

**Semantik**: Bei `let b = a;` wird `a` kopiert. `b.x = 10` ändert nicht `a.x`.

### 3.3 Classes (Reference-Typ)

```ebnf
ClassDecl       = [ 'pub' ] 'class' IDENTIFIER [ GenericParams ]
                  [ '::' InterfaceList ]
                  '{' [ ClassBody ] '}' .
ClassBody       = { ClassMember [ ',' ] } .                (* Trenner-Regel wie StructBody §3.2 *)
ClassMember     = Field | FunctionDecl .
```

Beispiel:

```lyr
pub class Player :: [Damageable, Serializable] {
    name: string,
    hp: int = 100,

    fn getName(): string {
        return this.name;
    }

    mut fn takeDamage(amount: int) {
        this.hp -= amount;
    }
}
```

**Semantik**: Bei `let b = a;` wird die Referenz geteilt. `b.takeDamage(10)` wirkt auf dasselbe Objekt wie `a`.

**Konstruktion**: Default-Konstruktor wird automatisch erzeugt aus den Feldern. Custom-Konstruktion via `new`-Methode (Konvention, keine Sprach-Magie):

```lyr
pub class Enemy {
    name: string,
    hp: int,

    fn new(name: string): Enemy {
        return Enemy { name = name, hp = 100 };
    }
}

let e = Enemy.new("goblin");
```

`new` ist kein Keyword — nur Konvention für Fabrik-Methoden.

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

Beispiel:

```lyr
pub enum Shape {
    Circle(float),
    Rectangle(float, float),
    Triangle { a: float, b: float, c: float },
    Empty;

    fn area(): float {
        return match (this) {
            Circle(r) => pi * r * r,
            Rectangle(w, h) => w * h,
            Triangle { a, b, c } => {
                let s = (a + b + c) / 2.0;
                return sqrt(s * (s-a) * (s-b) * (s-c));
            },
            Empty => 0.0,
        };
    }
}
```

Konstruktion über Voll-Pfad oder Kontext-Inferenz:

```lyr
let s: Shape = Shape.Circle(2.5);
let t: Shape = Triangle { a = 1.0, b = 1.0, c = 1.0 };   // Type kontextuell
```

### 3.5 Interfaces

```ebnf
InterfaceDecl   = [ 'pub' ] 'interface' IDENTIFIER [ GenericParams ]
                  '{' { InterfaceMember } '}' .
InterfaceMember = FunctionDecl .                                     (* Body optional = Default *)
```

Beispiel:

```lyr
pub interface Damageable {
    mut fn takeDamage(amount: int);
    fn isAlive(): bool {                                  // Default-Methode
        return this.getHp() > 0;
    }
    fn getHp(): int;                                       // abstrakt
}
```

- Nominales Subtyping: `struct X :: [I]` deklariert Konformität explizit.
- Default-Methoden sind überschreibbar.

### 3.6 Extend-Blöcke

```ebnf
ExtendDecl      = 'extend' TypeExpr [ '::' InterfaceList ]
                  '{' { FunctionDecl } '}' .
```

Beispiel:

```lyr
extend string {
    fn toIntSafely(): ?int {
        return parseInt(this);
    }
}

extend Player :: [Logger] {
    fn log(): string {
        return f"Player({this.name}, hp={this.hp})";
    }
}
```

Regeln:

- `extend T { ... }` fügt inherent Methoden zu `T` hinzu.
- `extend T :: [I] { ... }` fügt Interface-Impl zu `T` hinzu.
- Extensions gelten in jedem Modul, das das deklarierende Modul importiert. **Keine** separate Aktivierung.
- **Orphan-Rule**: Du darfst `extend T :: [I]` nur, wenn `T` oder `I` in deinem eigenen Modul deklariert wurde (`LYR-SEM0010`).

---

## 4. Typausdrücke

```ebnf
TypeExpr        = TypePrefix TypeAtom { TypeSuffix } .
TypePrefix      = [ '?' ] .                                    (* '?' = nullable *)
TypeAtom        = BuiltinType
                | ModulePath [ '<' TypeExpr { ',' TypeExpr } '>' ]    (* generisch *)
                | FunctionType
                | TupleType .
FunctionType    = 'fn' '(' [ TypeExpr { ',' TypeExpr } ] ')' '->' TypeExpr .
TupleType       = '(' TypeExpr ',' TypeExpr { ',' TypeExpr } ')' .   (* arity >= 2, keine Obergrenze *)
TypeSuffix      = '[' [ IntLit ] ']' .                          (* T[] oder T[N] *)

BuiltinType     = 'int' | 'uint' | 'float'
                | 'int8' | 'int16' | 'int32' | 'int64'
                | 'uint8' | 'uint16' | 'uint32' | 'uint64'
                | 'float32' | 'float64'
                | 'bool' | 'char' | 'string' | 'void' .
```

| Typ | Semantik |
|---|---|
| `int` | 64-bit signed, Standard-Integer |
| `uint` | 64-bit unsigned |
| `int8`/`int16`/`int32`/`int64` | explizite Größen |
| `uint8`/`.../uint64` | explizite Größen unsigned |
| `float` | 64-bit IEEE 754, Standard-Float |
| `float32`/`float64` | explizite Größen |
| `bool` | true/false |
| `char` | ein Unicode-Codepoint |
| `string` | UTF-8 Fat-Pointer `{ data, length }`, immutable |
| `void` | nur als Rückgabetyp |
| `?T` | nullable, äquivalent `Option<T>` |
| `T[]` | dynamisches Array (= `List<T>`-Slice) |
| `T[N]` | Fix-Size-Array mit Compile-Time-Länge N |
| `(A, B)`, `(A, B, C)`, … | Tupel (arity ≥ 2, keine Obergrenze) |
| `fn(A, B) -> R` | Funktionstyp / Closure-Slot |

Semantische Einschränkungen:

- `void` nur als Funktions-Rückgabe.
- `?T` und `T` sind verschiedene Typen, nicht implizit konvertierbar.
- `T` → `?T` ist implizit (Widening).
- `?T` → `T` braucht `expr!` (Force-Unwrap, kann werfen), `expr ?? default`, oder Pattern-Match.

---

## 5. Statements

```ebnf
Block           = '{' { Statement } '}' .

Statement       = Block
                | BindingStmt
                | IfStmt
                | WhileStmt
                | DoWhileStmt
                | ForInStmt
                | MatchStmt
                | BreakStmt
                | ContinueStmt
                | ReturnStmt
                | YieldStmt
                | ResumeStmt
                | DeferStmt
                | ThrowStmt
                | TryStmt
                | ExprStmt .

BindingStmt     = ( 'let' | 'var' ) IDENTIFIER [ ':' TypeExpr ] [ '=' Expr ] ';' .

IfStmt          = 'if' '(' Expr ')' Block [ 'else' ( Block | IfStmt ) ] .

WhileStmt       = 'while' '(' Expr ')' Block .
DoWhileStmt     = 'do' Block 'while' '(' Expr ')' ';' .

ForInStmt       = 'for' '(' IDENTIFIER 'in' Expr ')' Block .

MatchStmt       = 'match' '(' Expr ')' '{' { MatchArm } '}' .
MatchArm        = Pattern [ 'if' Expr ] '=>' ( Expr | Block ) .   (* Trenner: Expr-Arm ',' Pflicht (außer letzter vor '}'), Block-Arm ',' optional *)

BreakStmt       = 'break' ';' .
ContinueStmt    = 'continue' ';' .
ReturnStmt      = 'return' [ Expr ] ';' .
YieldStmt       = 'yield' [ Expr ] ';' .
ResumeStmt      = 'resume' Expr [ ',' Expr ] ';' .

DeferStmt       = 'defer' ( Block | Expr ';' ) .

ThrowStmt       = 'throw' Expr ';' .

TryStmt         = 'try' Block { CatchClause } .
CatchClause     = 'catch' '(' CatchBinding ')' Block .
CatchBinding    = '_'                                          (* catch-all ohne binding *)
                | IDENTIFIER ':' TypeExpr                       (* typed catch *)
                | IDENTIFIER .                                  (* catch-all mit binding (Throwable) *)

ExprStmt        = Expr ';' .                                    (* nur Call und Assign *)
```

Sema-Regeln (Auswahl):

- `let` ist immutable; `var` mutable. Beide Pflicht-Init in lokalen Scope, außer DAA beweist Init vor erstem Read.
- `for-in` benötigt einen Ausdruck, der das `Iterator<T>`-Interface implementiert.
- `match` ist exhaustive: alle Cases müssen abgedeckt sein oder `_` als Default (`LYR-SEM0050`).
- Blöcke haben keinen Wert. Ein Block-Arm in einem match-**Ausdruck** (§6.2) muss deshalb auf
  jedem Pfad die Funktion verlassen (`return`/`throw`) und trägt keinen Wert zur
  Arm-Unifikation bei (`LYR-SEM0033`); im match-**Statement** sind Block-Arme frei.
- `defer` registriert in LIFO-Reihenfolge, läuft auf jedem Scope-Exit (auch bei Exception).
- `yield` und `resume` sind nur in Coroutine-Funktionen erlaubt (siehe §8).
- `try` braucht mindestens ein `catch`. `finally` gibt es nicht — `defer` ist der einzige Cleanup-Mechanismus.

---

## 6. Ausdrücke

### 6.1 Präzedenz (höchste zuerst)

| # | Operatoren | Assoz. |
|---|---|---|
| 1 | Postfix `.` `?.` `[ ]` `( )` `++` `--` `!` (unwrap) | links |
| 2 | Prefix `!` (logical not) `-` `~` `++` `--` | rechts |
| 3 | `as` | links |
| 4 | `*` `/` `%` | links |
| 5 | `+` `-` | links |
| 6 | `<<` `>>` | links |
| 7 | `..` `..=` | nicht-assoz. |
| 8 | `&` | links |
| 9 | `^` | links |
| 10 | `\|` | links |
| 11 | `<` `<=` `>` `>=` | links |
| 12 | `==` `!=` | links |
| 13 | `&&` | links |
| 14 | `\|\|` | links |
| 15 | `??` | rechts |
| 16 | Assignments | rechts |

### 6.2 Grammatik (kompakt)

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
                | AT_IDENT [ '(' [ ArgList ] ')' ]
                | '(' Expr ')'
                | IfExpr
                | MatchExpr
                | StructInit
                | ArrayLit
                | TupleLit
                | Lambda .

Lambda          = '(' [ LambdaParam { ',' LambdaParam } ] ')' [ ':' TypeExpr ] '=>' ( Expr | Block ) .
LambdaParam     = IDENTIFIER [ ':' TypeExpr ] .

StructInit      = TypePath '{' [ StructInitField { ',' StructInitField } [ ',' ] ] '}' .  (* nicht am ExprStmt-Anfang erkannt (mehrdeutig mit Block); in jeder Wert-Position erlaubt *)
StructInitField = IDENTIFIER '=' Expr .                              (* '=' für Werte, ':' nur für Typen *)

TypePath        = ModulePath [ '<' TypeExpr { ',' TypeExpr } '>' ] . (* Typ-Referenz in Wert-/Pattern-Position: Stack<int>, game.Enemy. Generische Typen brauchen explizite Argumente (keine Feld-Inferenz); generische FUNKTIONEN dagegen inferieren aus den Argumenten — es gibt kein f<T>(x) (Turbofish) *)

ArrayLit        = '[' [ Expr { ',' Expr } [ ',' ] ] ']' .
TupleLit        = '(' Expr ',' Expr { ',' Expr } ')' .

IfExpr          = 'if' '(' Expr ')' Expr 'else' Expr .   (* Branches sind Ausdrücke (garantierter Wert); 'else' Pflicht; 'else if' = geschachteltes IfExpr. Für Statement-Blocks: IfStmt §5 *)

MatchExpr       = 'match' '(' Expr ')' '{' { MatchArm } '}' .   (* Wert kommt nur aus Expr-Armen; Block-Arme müssen return/throw-en, siehe §5 *)
```

### 6.3 Patterns (für `match`)

```ebnf
Pattern         = '_'                                  (* wildcard *)
                | Literal                              (* int, float, string, char, bool, null *)
                | IDENTIFIER                           (* bind-by-name *)
                | TypePath [ '(' Pattern { ',' Pattern } ')' ]
                | TypePath '{' [ FieldPattern { ',' FieldPattern } [ ',' ] ] '}'
                | TupleLit-of-Patterns
                | Pattern '|' Pattern                  (* or-pattern *)
                | RangePattern .                       (* 0..=9 *)
FieldPattern    = IDENTIFIER [ '=' Pattern ] .                       (* '=' für Pattern-Wert, ':' nur für Typen *)
```

Beispiele:

```lyr
match (shape) {
    Circle(r) if r < 1.0 => "tiny",
    Circle(r) => f"radius {r}",
    Rectangle(w, h) if w == h => "square",
    Rectangle(w, h) => f"{w}x{h}",
    Triangle { a, b, c } => "triangle",
    _ => "other",
}
```

### 6.4 Lvalue-Regeln

Linke Seite eines Assignments:
- `IDENTIFIER` (das gebundene Symbol muss `var` sein)
- `Postfix '.' IDENTIFIER` (Feld muss mut sein: `class`-Feld, oder `mut fn`-Methode für `struct`)
- `Postfix '[' Expr ']'` (Container muss mut sein)
- `( Lvalue )`

### 6.5 Operator- und Konvertierungs-Semantik (Typen)

Die Typregeln der Operatoren (von der Sema durchgesetzt):

- **Numerik ist strikt.** Arithmetik (`+ - * / %`), Bitweise (`& | ^ << >>`) und
  Vergleiche verlangen **denselben** numerischen Typ auf beiden Seiten. Es gibt
  **kein** implizites Widening (`int8` → `int32` braucht `as`). Ausnahme: ein
  *untyped* Ganzzahl-/Float-Literal (ohne Suffix) passt sich dem Kontext an, sofern
  sein Wert hineinpasst — `let x: int8 = 5;` ist ok, `= 300` nicht.
- **`+` und `*` überladen für `string` und `T[]`**: `string + string` / `T[] + T[]`
  = Konkatenation; `string * int` / `T[] * int` = Wiederholung (`[0] * 5`). Ergebnis
  ist dynamisch (`string` bzw. `T[]`). Iterativer Aufbau geht besser über
  `std.string.StringBuilder` / `join`. (Das ist *eingebaute* Semantik, kein
  user-defined Overloading — das bleibt post-v1.)
- **`as`** konvertiert in v1 nur **Numerik ↔ Numerik** (alle Größen, int ↔ float).
- **Vergleiche/Logik** liefern `bool`; `&&`/`||` verlangen `bool`-Operanden.
- **Nullable** (§7): `T` → `?T` implizit; `?T` → `T` nur via `!`, `??` oder
  Pattern-Match. Flow-Narrowing (`if (x != null)`) siehe §7.

---

## 7. Nullable und Optional-Operationen

| Syntax | Bedeutung |
|---|---|
| `?T` | äquivalent zu `Option<T>` |
| `expr?.member` | optional chaining, Ergebnistyp `?U` |
| `a ?? b` | Null-Coalescing |
| `a ??= b` | Null-Coalescing-Assign |
| `expr!` | Force-Unwrap, wirft `NullDereferenceError` bei null |

Narrowing: `if (x != null) { … }` engt `x: ?T` im then-Zweig zu `x: T` ein.

---

## 8. Coroutinen

```lyr
fn fibonacci(): Coroutine<int> {
    var a = 0;
    var b = 1;
    while (true) {
        yield a;
        let next = a + b;
        a = b;
        b = next;
    }
}

let co = fibonacci();
for (i in 0..10) {
    let v = resume co;
    println(v);
}
```

Regeln (Sema):

- Eine Funktion ist Coroutine, wenn sie `yield` enthält oder Rückgabetyp `Coroutine<T>` hat.
- `yield expr;` pausiert die Coroutine und liefert `expr` an den Aufrufer.
- `resume co` und `resume co, value` setzen die Coroutine fort, optional mit einem Wert, der an `yield` zurückgegeben wird.
- Coroutine endet, wenn der Body durchläuft. Weitere `resume`-Aufrufe werfen `CoroutineEndedError`.

---

## 9. Exceptions

```lyr
class FileNotFound :: [Throwable] {
    path: string,
    fn message(): string {
        return f"file not found: {this.path}";
    }
}

fn readFile(path: string): string throws FileNotFound {
    if (!exists(path)) {
        throw FileNotFound { path = path };
    }
    return io.readText(path);
}

fn main(): int {
    try {
        let content = readFile("config.json");
        println(content);
    } catch (e: FileNotFound) {
        eprintln(f"oops: {e.message()}");
    } catch (e) {
        eprintln(f"unknown error: {e}");
    }
    return 0;
}
```

Regeln:

- Nur Typen, die `Throwable` implementieren, sind werfbar (`LYR-SEM0030`).
- `throws TypeName` in Signatur: Funktion kann nur diesen Typ (oder seine Subtypes-via-Interface) werfen.
- `throws` ohne Typ: Funktion kann jeden Throwable werfen.
- Aufrufe von `throws`-Funktionen brauchen entweder eigene `throws`-Deklaration (auto-propagation) oder umgebenden `try`.
- Catch-All (`catch (e)` oder `catch (_)`) muss die **letzte** Catch-Klausel sein.

Plus `panic(msg)` für nicht-catchbare Programm-Bugs:

```lyr
fn divide(a: int, b: int): int {
    if (b == 0) { panic("division by zero"); }
    return a / b;
}
```

`panic` ist Sprach-Built-in mit Rückgabetyp `never`, nicht via `try` abfangbar.

---

## 10. Compile-Time-Built-ins (`@name`)

In v1 sind nur **Attribute** als `@name` syntaktisch erlaubt. Built-ins, die compile-time werten, gibt es nicht (Oils `@import`/`@using`-Konstrukte gibt es bei uns als reguläre `import`-Statements, nicht als Built-ins).

### 10.1 Stdlib-Attribute

| Attribut | Anwendung | Wirkung |
|---|---|---|
| `@test` | Funktion | Markiert Test-Case, ausgeführt von `lyric test` |
| `@deprecated(reason)` | Funktion, Typ, Feld | Warnung am Aufrufort |
| `@inline` | Funktion | Hint an VM, inline zu interpretieren |
| `@cold` | Funktion | Hint: selten ausgeführt |
| `@noCapture` | Lambda-Parameter | Verbietet implizites Capture (Performance/Safety) |

User-defined Attribute sind **post-v1**.

---

## 11. Entry-Contract

Genau **eine** Signatur pro Standalone-Executable:

```lyr
fn main(): int { … }
fn main(args: string[]): int { … }
```

Rückgabewert = Prozess-Exit-Code (0..255).

Module, die nur als Library oder Embed-Script dienen, brauchen kein `main`.

---

## 12. Diagnostik-Code-Präfixe

| Präfix | Bedeutung |
|---|---|
| `LYR-LEX####` | Lexer |
| `LYR-PAR####` | Parser |
| `LYR-RES####` | Modul-Resolver |
| `LYR-SEM####` | Semantik / Typsystem |
| `LYR-IR####` | IR-Lowering |
| `LYR-BC####` | Bytecode-Codegen |
| `LYR-VM####` | Runtime-Fehler |
| `LYR-CAP####` | Capability-Verletzung |
| `LYR-CLI####` | CLI-/Build-Fehler |

---

## 13. Bewusst nicht in v1

| Feature | Wann |
|---|---|
| User-defined Operator-Overloading | v1.X |
| User-defined Attribute / Macros | post-v1 oder nie |
| Async/Await-Syntax | v1.X (Coroutines genügen) |
| Reflection (run-time) | post-v1 |
| `unsafe` / Raw-Pointer | nie (wir sind GC-VM) |
| Direkte FFI (`@extern`/`DllImport`) | nie (Host-Bindings stattdessen) |
| LSP-Server | v1.2 |
| Formatter | v1.X |
| Package-Manager | nur bei demonstriertem Bedarf |
| Named Arguments | v1.X |
| `pub(package)`-Sichtbarkeit | v1.1 |
| Raw-Strings `r"..."` | v1.1 |
| Newtypes | v1.1 |
| Inheritance (class-extends-class) | **nie** |
