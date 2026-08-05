# Lyric – Sprach-Dokumentation

> Diese Datei richtet sich an **Nutzer** der Sprache **Lyric** (Compiler `lyric`, Datei-Endung `.lyr`). Sie beschreibt Syntax, Typsystem und Features in erklärendem Stil mit Beispielen. Die formelle Grammatik liegt in [`Sprache.md`](Sprache.md), Architektur und Meilensteine in [`ROADMAP.md`](ROADMAP.md).

## Inhaltsverzeichnis

1. [Überblick](#1-überblick)
2. [Hello World](#2-hello-world)
3. [Module, Imports und Sichtbarkeit](#3-module-imports-und-sichtbarkeit)
4. [Bindings: `let` und `var`](#4-bindings-let-und-var)
5. [Typsystem](#5-typsystem)
6. [Nullable und Optional](#6-nullable-und-optional)
7. [Literale und String-Interpolation](#7-literale-und-string-interpolation)
8. [Operatoren und Präzedenz](#8-operatoren-und-präzedenz)
9. [Kontrollfluss](#9-kontrollfluss)
10. [Funktionen und Methoden](#10-funktionen-und-methoden)
11. [Lambdas und Closures](#11-lambdas-und-closures)
12. [Structs und Classes](#12-structs-und-classes)
13. [Interfaces und Default-Methoden](#13-interfaces-und-default-methoden)
14. [Enums und Pattern-Matching](#14-enums-und-pattern-matching)
15. [Extend-Blöcke](#15-extend-blöcke)
16. [Generics](#16-generics)
17. [Exceptions](#17-exceptions)
18. [Defer](#18-defer)
19. [Coroutinen](#19-coroutinen)
20. [Capabilities und FFI](#20-capabilities-und-ffi)
21. [Embedding (für Hosts)](#21-embedding-für-hosts)
22. [Standardbibliothek-Überblick](#22-standardbibliothek-überblick)
23. [CLI-Befehle](#23-cli-befehle)
24. [Was kommt nach v1?](#24-was-kommt-nach-v1)

---

## 1. Überblick

**Lyric** ist eine statisch typisierte Sprache mit Bytecode-VM. Sie ist für zwei Use-Cases gemacht:

1. **Standalone-Apps** — CLI-Tools, Desktop-Anwendungen, Server.
2. **Embedded in Hosts** — Game-Engines, Editoren, Tools, die Lyric-Scripts laden und ausführen.

Beide Use-Cases nutzen denselben Compiler, dieselbe Sprache, dieselbe Stdlib. Der Unterschied: im embedded-Modus entscheidet der Host, welche Capabilities (Datei-Zugriff, Netzwerk, OS, .NET-Reflection) das Script bekommt.

**Sprach-DNA**:
- Statisch typisiert, lokale Typ-Inferenz.
- GC-managed (über .NET-Runtime).
- Modern OOP-arm: `struct` (Value) + `class` (Reference), beide ohne Inheritance.
- Polymorphie über Interfaces mit Default-Methoden.
- Generics, Pattern-Matching, Closures, Coroutinen.
- Typed Exceptions (mit `throws` in Signatur, wie Swift).
- Capabilities-basiertes FFI/Stdlib-Modell.

**Was Lyric bewusst nicht hat** (siehe [§24](#24-was-kommt-nach-v1)):
- Class-Inheritance (für immer).
- `unsafe` / Raw-Pointer (für immer).
- Direkte FFI (`@extern` o.ä.) — Host registriert Bindings.
- Threads in der Sprache — Coroutinen statt dessen.

---

## 2. Hello World

```lyr
// examples/hello.lyr
import std.io.console;

fn main(): int {
    let name = "world";
    console.println(f"hello, {name}!");
    return 0;
}
```

Bauen und ausführen:

```bash
lyric run examples/hello.lyr
# Ausgabe: hello, world!
```

Nur Bytecode generieren:

```bash
lyric build examples/hello.lyr -o hello.lyrbc
lyric run hello.lyrbc
```

---

## 3. Module, Imports und Sichtbarkeit

### 3.1 Module aus Dateien

Eine Datei = ein Modul. Der Modulname wird aus dem Pfad relativ zum Source-Root abgeleitet:

```
src/main.lyr            → Modul main
src/game/player.lyr     → Modul game.player
src/game/world/tile.lyr → Modul game.world.tile
```

Optional kann eine Datei einen expliziten Header haben:

```lyr
module game.entities.player;
```

Wenn der Header existiert, muss er zum Pfad passen — sonst Fehler `LYR-RES0001`.

### 3.2 Imports

Drei Formen:

```lyr
// 1) Namespace-Import — Modul als Namespace ansprechen
import std.io.console;
console.println("hi");

// 2) Selektiver Import — einzelne Symbole direkt verfügbar
import std.io.console { println, eprintln };
println("hi");

// 3) Alias-Import — kürzerer Name
import std.collections.HashMap as Dict;
let d = Dict<string, int>();
```

Wildcard-Imports (`import std.io.*`) gibt es **nicht**.

### 3.3 Sichtbarkeit

`pub` exportiert ein Symbol, ohne Marker ist es modul-privat:

```lyr
pub fn createPlayer(name: string): Player { ... }      // exportiert
fn validateName(s: string): bool { ... }                // privat
```

Auf Top-Level ist Default-privat — die häufigere Wahl wird kürzer.

---

## 4. Bindings: `let` und `var`

| Form | Mutabilität | Init |
|---|---|---|
| `let x: T = expr;` | immutable | Pflicht (außer DAA beweist) |
| `var x: T = expr;` | mutable | Pflicht (außer DAA beweist) |
| `var x: T;` | mutable | später, wenn Compiler beweist dass jeder Read eine Zuweisung sieht |

```lyr
let pi: float = 3.14159;
var count: int = 0;
count += 1;       // OK
// pi = 3.14;     // Fehler: let ist immutable
```

Typinferenz greift, wenn der Initializer da ist:

```lyr
let n = 42;          // n: int
let s = "hi";        // s: string
let nums = [1, 2, 3]; // nums: int[]
```

Globale Bindings auf Modul-Ebene dürfen nur `let` sein (kein globales Mutable-State — wenn du das brauchst, mach ein `class` mit static-artigem Lookup).

---

## 5. Typsystem

### 5.1 Primitive Typen

| Kategorie | Default-Typ | Sized-Varianten |
|---|---|---|
| Signed Integer | `int` (=int64) | `int8`, `int16`, `int32`, `int64` |
| Unsigned Integer | `uint` (=uint64) | `uint8`, `uint16`, `uint32`, `uint64` |
| Float | `float` (=float64) | `float32`, `float64` |
| Boolean | `bool` | — |
| Char (1 Codepoint) | `char` | — |
| String (UTF-8) | `string` | — |
| Void (nur Return) | `void` | — |

### 5.2 Konstruierte Typen

```lyr
let xs: int[]          = [1, 2, 3];           // dynamisches Array
let buf: byte[16]      = [0, 0, ..., 0];      // fixed-size Array
let pair: (int, string) = (42, "hi");          // Tupel (max arity 3)
let user: ?User        = findUser(id);         // nullable
let fn: fn(int) -> int = (x) => x * 2;         // Funktionstyp
```

`T[]` ist ein **echtes Array**: die Länge steht bei der Erzeugung fest und ändert sich nicht mehr
(ADR-016). Du kannst indizieren (`xs[0]`) und `.length` lesen.

Gebaut wird es auf drei Arten — alle drei erzeugen ein neues Array:

```lyr
let a = [3, 7, 1];      // Literal
let b = [0] * n;        // n Elemente, alle 0 — n darf ein Laufzeitwert sein
let c = a + [9];        // Konkatenation
```

Wenn du etwas brauchst, das **wächst**, nimm `std.collections.List<T>`. Es hält intern ein `T[]`,
kopiert bei Bedarf um und kann `.push(v)`/`.pop()`. Der Index-Operator funktioniert dort genauso —
`List<T>` implementiert das `Indexable<T>`-Interface, an das `[i]` für alles außer `T[]` bindet.

### 5.3 Type-Aliases

```lyr
pub type UserId = int;
pub type Score = float;

let id: UserId = 42;
let s: Score = id as Score;     // explizit, weil Type-Alias keine nominale Trennung ist
```

`type` ist purer Alias, keine Newtype-Magie. Für nominale Trennung kommen Newtypes erst v1.1.

### 5.4 Casts

- Implizit nur in unproblematischen Fällen (`T` → `?T`, Integer-Widening).
- Explizit über `as`:

```lyr
let n: int64 = 1_000_000;
let s: int16 = n as int16;     // narrowing, kann truncaten
let f: float = n as float;
```

---

## 6. Nullable und Optional

`?T` ist äquivalent zu `Option<T>` aus der Stdlib. Vier Operatoren:

```lyr
import std.io.console;

var user: ?User = findUser(id);

// 1) Narrowing: nach != null gilt user: User im Block
if (user != null) {
    console.println(user.name);
}

// 2) Optional-Chaining: Ergebnis ist ?string
let maybeName: ?string = user?.name;

// 3) Coalescing
let name: string = user?.name ?? "unknown";

// 4) Coalescing-Assign
var label: ?string = null;
label ??= "default";

// 5) Force-Unwrap mit !
let must: User = user!;        // wirft NullDereferenceError bei null
```

Alternativ Pattern-Match:

```lyr
match (user) {
    null => console.println("no user"),
    u => console.println(u.name),
}
```

---

## 7. Literale und String-Interpolation

```lyr
// Integer
let dec  = 42;
let hex  = 0xCAFE_BABE;
let bin  = 0b1010_0011;
let oct  = 0o755;
let big  = 1_000_000;
let i32  = 100i32;
let u64  = 42u64;

// Float
let pi   = 3.14;
let e    = 1e10;
let f32v = 0.5f32;

// String / Char / Bool / Null
let s    = "Hello, \u{1F30D}";
let c    = '\n';
let on   = true;
let none = null;

// String-Interpolation (v1-Feature)
let greeting = f"hello, {name}! score: {score:N2}";
let mathy    = f"{a} + {b} = {a + b}";
```

Format-Spec im Interpolations-Block (`{value:spec}`) folgt dem .NET-Format:

| Spec | Bedeutung |
|---|---|
| `{x:N2}` | Nummer mit 2 Dezimalen |
| `{x:0>5}` | rechts-aligned, min-width 5, padding `0` |
| `{x:X}` | Hex, uppercase |
| `{x:%}` | Prozent |

---

## 8. Operatoren und Präzedenz

Höchste zuerst:

1. Postfix: `.`, `?.`, `[ ]`, `( )`, `++`, `--`, `!` (unwrap)
2. Prefix: `!` (logical not), `-`, `~`, `++`, `--`
3. `as`
4. `*` `/` `%`
5. `+` `-`
6. `<<` `>>`
7. `..` `..=`
8. `&`
9. `^`
10. `|`
11. `<` `<=` `>` `>=`
12. `==` `!=`
13. `&&`
14. `||`
15. `??`
16. Assignments (rechts-assoziativ): `=`, `+=`, `-=`, `*=`, `/=`, `%=`, `&=`, `|=`, `^=`, `<<=`, `>>=`, `&&=`, `||=`, `??=`

Ranges als eigene Operatoren:

```lyr
for (i in 0..10)   { ... }   // 0, 1, ..., 9
for (i in 0..=10)  { ... }   // 0, 1, ..., 10
```

---

## 9. Kontrollfluss

### 9.1 If/Else (Statement & Expression)

Als **Statement** sind die Zweige Blöcke:

```lyr
if (n > 0) { ... } else if (n < 0) { ... } else { ... }
```

Als **Ausdruck** sind die Zweige Ausdrücke — <b>keine</b> Blöcke, und `else` ist Pflicht:

```lyr
let sign: int = if (n > 0) 1 else if (n < 0) -1 else 0;
```

Der Unterschied ist kein Schönheitsfehler: **Blöcke haben in Lyric keinen Wert** (dieselbe Regel
wie bei den match-Armen, [§14.2](#142-match-arme-ausdruck-oder-block)). `{ 1 }` ist deshalb kein
Ausdruck, der `1` liefert, sondern ein Block — und ein Block kann rechts von `=` nicht stehen.
`if (n > 0) { 1 } else { 0 }` ist ein Syntaxfehler (`LYR-PAR0002`).

Wenn ein Zweig mehr als einen Ausdruck braucht, gilt derselbe Ausweg wie beim `match`: eine
Helper-Funktion aufrufen, oder das if als **Statement** schreiben und in eine `var` zuweisen.

### 9.2 Schleifen

```lyr
while (cond) { ... }

do { ... } while (cond);

for (x in xs)        { ... }
for (i in 0..n)      { ... }
for (entry in map)   { ... }    // Iterator über key-value-pairs
```

Kein klassisches `for (i = 0; i < n; i++)` — `for-in` plus `0..n` deckt das ab.

### 9.3 Break/Continue/Return

```lyr
for (x in xs) {
    if (x < 0) { continue; }
    if (x > 100) { break; }
    process(x);
}

fn first(xs: int[]): ?int {
    for (x in xs) { return x; }
    return null;
}
```

### 9.4 Match (Statement & Expression)

Siehe [§14 Pattern-Matching](#14-enums-und-pattern-matching).

---

## 10. Funktionen und Methoden

### 10.1 Freie Funktionen

```lyr
pub fn add(a: int, b: int): int {
    return a + b;
}

pub fn greet(name: string = "world"): string {
    return f"hello, {name}!";
}

pub fn sum(params nums: int[]): int {
    var total = 0;
    for (n in nums) { total += n; }
    return total;
}

sum(1, 2, 3, 4);     // 10
```

- Rückgabetyp ist Pflicht (`void` möglich).
- Default-Werte nur an trailing Parametern.
- `params name: T[]` sammelt variable Argumente (nur am letzten Parameter).

### 10.2 Methoden in Structs/Classes

Methoden leben im Body der Struct/Class-Deklaration:

```lyr
pub class Counter {
    value: int = 0,

    fn get(): int {
        return this.value;
    }

    mut fn increment() {
        this.value += 1;
    }

    mut fn add(amount: int) {
        this.value += amount;
    }
}

var c = Counter { };
c.increment();        // OK, value = 1
c.add(10);            // OK, value = 11
let v = c.get();      // OK, v = 11
```

- `this` ist implizit der Receiver (kein expliziter `self`-Parameter).
- `mut fn` markiert Methoden, die `this` mutieren. Wichtig vor allem für `struct` (Value-Typen).
- Für `class` ist `mut fn` zwar erlaubt aber semantisch immer egal (Reference-Mutation ist immer möglich) — Konvention: trotzdem `mut fn` markieren, um Lesbarkeit zu erhöhen.

### 10.3 Konstruktion

Default-Konstruktor wird automatisch aus den Feldern generiert:

```lyr
let p = Player { name = "alice", hp = 100 };
```

Für komplexere Konstruktion: eine `static`-Fabrik. Der Name `new` ist kein Keyword, nur Konvention —
`static` dagegen ist Pflicht, sonst wäre es eine Instanzmethode und bräuchte ein Objekt:

```lyr
pub class Enemy {
    name: string,
    hp: int,

    static let BASE_HP: int = 10;

    static fn new(level: int): Enemy {
        return Enemy { name = f"goblin-{level}", hp = Enemy.BASE_HP * level };
    }
}

let e = Enemy.new(5);
```

`static let` gibt einem Typ Konstanten, die zu ihm gehören: `Enemy.BASE_HP` statt eines
Modul-`let` mit sprechendem Präfix. Mehrere Fabriken sind kein Problem — sie brauchen nur
verschiedene Namen (`Enemy.new`, `Enemy.fromSave`), weil Lyric in v1 kein Overloading hat
(ADR-015).

---

## 11. Lambdas und Closures

```lyr
let inc = (x: int) => x + 1;                    // annotierte Parameter
let pair = (a: int, b: int): int => a * b;      // + annotierter Rückgabetyp

// Parameter-Typen dürfen wegfallen, wenn der Kontext sie liefert:
let f: fn(int) -> int = (x) => x + 1;           // x: int aus dem Binding-Typ

// Closure mit implizitem Capture
let factor = 3;
let scale = (x: int) => x * factor;             // fängt factor ein
let result = scale(10);                          // 30
```

**Zwei Body-Formen:**

- `(params) => expr` — der Ausdruck *ist* der Wert.
- `(params) => { stmts }` — ein Block. Blöcke haben keinen Wert (dieselbe Regel wie bei
  `match`, siehe [§14.2](#142-match-arme-ausdruck-oder-block)): ein Block-Lambda liefert
  sein Ergebnis über `return`, und ein nicht-void-Block-Lambda muss auf jedem Pfad returnen.
  Ein Block-Lambda ohne `return <wert>` ist ein Seiteneffekt-Lambda und automatisch `void`
  — auch ohne Kontext: `let log = () => { console.println("hi"); };`.

```lyr
let clamp: fn(int) -> int = (x) => {
    if (x < 0) { return 0; }
    return x;
};
```

**Regeln:**

- **Parameter-Typen** kommen aus einer Annotation oder aus dem Kontext (Binding-Typ,
  Aufruf-Argument, Rückgabeposition). Fehlt beides, ist der Parameter ein Fehler
  (`LYR-SEM0045`) — dann annotieren: `(x: int) => …`.
- **Block-Lambdas, die einen Wert liefern,** brauchen ihren Rückgabetyp aus Annotation oder
  Kontext (`LYR-SEM0046`); wertlose Block-Lambdas sind `void`.
- **`return` in einem Lambda** verlässt das Lambda, nicht die umgebende Funktion.
- **Captures** sind implizit und müssen am Erzeugungsort bereits sicher zugewiesen sein —
  dieselbe Definite-Assignment-Regel wie bei Variablen.

Captures sind implizit (ADR-011). Wenn das performance-relevant wird (z.B. häufig erzeugte
Game-Logic-Lambdas), kann post-v1 ein `@noCapture`-Marker kommen.

---

## 12. Structs und Classes

Lyric hat zwei Typ-Kategorien für eigene Datentypen, die sich **nur in der Semantik** unterscheiden:

| Aspekt | `struct` | `class` |
|---|---|---|
| Zuweisungs-Semantik | wird kopiert | Referenz wird geteilt |
| Identität | keine | hat Identität |
| Allokation | Stack/inline (wenn möglich) | Heap |
| Mutability über Receiver | braucht `mut fn` | `mut fn` ist Lesbarkeits-Marker |
| Inheritance | **nicht möglich** | **nicht möglich** |
| Interface-Impl | über `::` | über `::` |

### 12.1 Struct-Beispiel (Game-Math)

```lyr
pub struct Vector3 :: [Equatable] {
    x: float,
    y: float,
    z: float,

    fn length(): float {
        return sqrt(this.x*this.x + this.y*this.y + this.z*this.z);
    }

    fn dot(other: Vector3): float {
        return this.x*other.x + this.y*other.y + this.z*other.z;
    }

    fn equals(other: Vector3): bool {
        return this.x == other.x && this.y == other.y && this.z == other.z;
    }
}

let a = Vector3 { x = 1.0, y = 0.0, z = 0.0 };
let b = a;             // KOPIE
b.x = 99.0;            // ändert nicht a
console.println(a.x);  // 1.0
```

### 12.2 Class-Beispiel (Game-Entity)

```lyr
pub class Player :: [Damageable] {
    name: string,
    hp: int = 100,
    position: Vector3 = Vector3 { x = 0.0, y = 0.0, z = 0.0 },

    fn getName(): string {
        return this.name;
    }

    mut fn takeDamage(amount: int) {
        this.hp -= amount;
        if (this.hp < 0) { this.hp = 0; }
    }

    fn isAlive(): bool {
        return this.hp > 0;
    }
}

let p = Player { name = "hero" };
let q = p;             // REFERENZ
q.takeDamage(20);
console.println(p.hp); // 80  — p und q sind dasselbe Objekt
```

### 12.3 Wann struct, wann class?

- **Struct**: kleine Werte ohne Identität (Vektoren, Farben, Koordinaten, Schlüssel-Paare, Konfigurations-Snapshots). Spart Heap-Allokationen.
- **Class**: alles mit Lifecycle, Identität, Mutation (Entities, Manager, Pools, Resources).

Faustregel: wenn ein Objekt „etwas in der Welt" repräsentiert und mit anderen Modulen geteilt wird, ist es `class`. Wenn es ein Datenpaket ist, das nur weitergereicht wird, ist es `struct`.

---

## 13. Interfaces und Default-Methoden

```lyr
pub interface Damageable {
    mut fn takeDamage(amount: int);
    fn getHp(): int;

    // Default-Methode — überschreibbar
    fn isAlive(): bool {
        return this.getHp() > 0;
    }
}

pub class Player :: [Damageable] {
    hp: int = 100,
    mut fn takeDamage(amount: int) { this.hp -= amount; }
    fn getHp(): int { return this.hp; }
    // isAlive() wird vom Default geerbt
}

pub class Wall :: [Damageable] {
    hp: int = 50,
    mut fn takeDamage(amount: int) { this.hp -= amount; }
    fn getHp(): int { return this.hp; }
    fn isAlive(): bool {                    // overridet Default
        return this.hp > 10;                 // Wall gilt erst ab 10hp als "tot"
    }
}
```

- Interfaces deklarieren nur Methoden-Signaturen (mit optionalem Body als Default).
- Nominales Subtyping: `class X :: [I]` erklärt Konformität explizit.
- Dynamic Dispatch via Interface-Referenz: `let xs: Damageable[] = [player, wall];`.

---

## 14. Enums und Pattern-Matching

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

let s = Shape.Circle(2.5);
console.println(f"area = {s.area()}");
```

### 14.1 Pattern-Match-Power-Features

```lyr
fn classify(n: int): string {
    return match (n) {
        0                => "zero",
        1 | 2 | 3        => "small",
        n if n < 0       => "negative",
        4..=10           => "medium",
        _                => "large",
    };
}

fn describe(s: Shape): string {
    return match (s) {
        Circle(r) if r < 1.0           => "tiny circle",
        Circle(r)                      => f"circle r={r}",
        Rectangle(w, h) if w == h      => "square",
        Rectangle(w, h)                => f"rect {w}x{h}",
        Triangle { a, b, c }           => f"triangle {a}/{b}/{c}",
        Empty                          => "nothing",
    };
}
```

Pattern-Formen:
- Literal: `42`, `"hi"`, `true`, `null`
- Wildcard: `_`
- Identifier-Binding: `n`
- Tuple: `(a, b)`, `(a, _, c)`
- Struct-Destructuring: `Point { x, y }`, `Point { x = 0, y }`
- Enum-Variant: `Circle(r)`, `Rectangle(w, h)`, `Triangle { a, b, c }`
- Or-Pattern: `1 | 2 | 3`
- Range-Pattern: `0..=10`
- Guard: `n if n > 0`

**Exhaustivity** ist Pflicht: wenn nicht alle Fälle abgedeckt sind, gibt es einen Compile-Fehler `LYR-SEM0050` mit Liste der fehlenden Patterns.

### 14.2 Match-Arme: Ausdruck oder Block

Ein Match-Arm ist entweder ein **Ausdruck** (`Circle(r) => pi * r * r`) oder ein **Block** (`... => { ... }`). Der Unterschied zählt, sobald der `match` selbst ein Ausdruck ist (`let x = match (...)`, `return match (...)`):

**Blöcke haben in Lyric keinen Wert.** Es gibt keine Rust-artige „letzter Ausdruck ist der Block-Wert“-Regel. Der Wert eines Match-Ausdrucks kommt deshalb ausschließlich aus Ausdrucks-Armen. Ein Block-Arm ist im Match-Ausdruck trotzdem erlaubt — aber nur, wenn er auf jedem Pfad die Funktion verlässt (`return` oder `throw`). So macht es `Shape.area()` oben: der Triangle-Arm rechnet in Ruhe und `return`t direkt aus der Funktion, am `match` vorbei. Ein Block-Arm, der einfach „durchläuft“, ist ein Compile-Fehler `LYR-SEM0033`:

```lyr
let label = match (s) {
    Circle(r) => f"Kreis({r})",
    Triangle { a, b, c } => {
        let u = a + b + c;          // Fehler LYR-SEM0033: dieser Block
    },                              // liefert keinen Wert für 'label'
    _ => "sonstiges",
};
```

Wenn ein Arm mehr als einen Ausdruck braucht, gibt es zwei idiomatische Wege:

```lyr
// 1) Helper-Funktion — der Arm bleibt ein Ausdruck
let label = match (s) {
    Circle(r)            => f"Kreis({r})",
    Triangle { a, b, c } => triangleLabel(a, b, c),
    _                    => "sonstiges",
};

// 2) match als STATEMENT + var — im Statement sind Block-Arme frei
var label: string;
match (s) {
    Circle(r) => label = f"Kreis({r})",
    Triangle { a, b, c } => {
        let u = a + b + c;
        label = f"Dreieck(U={u})";
    }
    _ => label = "sonstiges",
}
```

Der Compiler weiß bei einem exhaustiven `match`, dass genau ein Arm läuft — `label` gilt nach Variante 2 als sicher zugewiesen.

---

## 15. Extend-Blöcke

Methoden zu existierenden Typen hinzufügen — auch zu Builtins:

```lyr
extend string {
    fn toIntSafely(): ?int {
        return parseInt(this);
    }

    fn reverse(): string {
        // ...
    }
}

let n: ?int = "42".toIntSafely();   // some 42
let s = "hello".reverse();           // "olleh"
```

Interface-Konformität auch im Extend-Block:

```lyr
extend Player :: [Logger] {
    fn log(): string {
        return f"Player({this.name}, hp={this.hp})";
    }
}
```

**Orphan-Rule**: Du darfst `extend T :: [I]` nur, wenn entweder `T` oder `I` in deinem Modul deklariert wurde. Sonst Fehler `LYR-SEM0010`.

Extension-Methoden gelten **automatisch** in jedem Modul, das das deklarierende Modul importiert — keine `@using`-Aktivierung wie in Oil.

---

## 16. Generics

Generics gibt es in v1 für Funktionen, Strukturen, Klassen, Enums und Interfaces.

```lyr
pub fn first<T>(xs: T[]): ?T {
    if (xs.length == 0) { return null; }
    return xs[0];
}

pub class Stack<T> {
    items: T[] = [],

    mut fn push(value: T) {
        this.items.push(value);
    }

    mut fn pop(): ?T {
        if (this.items.length == 0) { return null; }
        return this.items.pop();
    }
}

pub interface Container<T> {
    mut fn add(item: T);
    fn count(): int;
}
```

### 16.1 Constraints

```lyr
pub interface Comparable {
    fn compareTo(other: Comparable): int;
}

pub fn max<T :: [Comparable]>(a: T, b: T): T {
    return if (a.compareTo(b) >= 0) a else b;
}
```

Mehrere Constraints: `<T :: [I1, I2]>`.

### 16.2 Implementierung

Generics werden zu Bytecode-Zeit **monomorphisiert** (eine Instanz pro Type-Argument-Set). Das heißt: zur Runtime gibt es keinen Generics-Overhead. Cost ist Bytecode-Größe — Vorteil ist Performance.

---

## 17. Exceptions

Lyric nutzt **Typed Exceptions** mit expliziten `throws`-Deklarationen.

```lyr
pub class FileNotFound :: [Throwable] {
    path: string,
    fn message(): string {
        return f"file not found: {this.path}";
    }
}

pub class PermissionDenied :: [Throwable] {
    fn message(): string { return "permission denied"; }
}

pub fn readFile(path: string): string throws FileNotFound {
    if (!exists(path)) {
        throw FileNotFound { path = path };
    }
    return io.readText(path);
}

fn main(): int {
    try {
        let content = readFile("config.json");
        console.println(content);
    } catch (e: FileNotFound) {
        console.eprintln(f"oops: {e.message()}");
    } catch (e: Throwable) {
        console.eprintln(f"unknown error: {e.message()}");
    }
    return 0;
}
```

Regeln:

- Nur `Throwable`-Typen sind werfbar.
- Funktionen mit `throws X` werfen nur `X` (oder `X`-konforme Subtypes).
- Funktionen mit `throws` (ohne Typ) werfen beliebige Throwables.
- Aufruf einer `throws`-Funktion **muss** entweder selbst `throws`-deklariert sein oder von `try` umgeben sein.
- Catch-All (`catch (e: Throwable)` oder `catch (_)`) muss die letzte Catch-Klausel sein.

### 17.1 Panic vs. Throw

`throw` ist für **Domain-Errors** (catchbar). `panic` ist für **Programm-Bugs** (nicht catchbar):

```lyr
pub fn divide(a: int, b: int): int {
    if (b == 0) {
        panic("division by zero — caller violated contract");
    }
    return a / b;
}
```

Ein `panic` läuft alle `defer`s auf dem Stack ab, druckt Backtrace, beendet die VM (oder propagiert an den Host bei Embedded). Es ist **nicht** mit `try/catch` abfangbar.

---

## 18. Defer

`defer` registriert einen Ausdruck oder Block zur Ausführung beim Scope-Exit. LIFO-Reihenfolge. Läuft auf jedem Pfad (normal, `return`, Exception).

```lyr
fn processFile(path: string): string throws FileNotFound {
    let file = openFile(path);
    defer file.close();              // läuft bei jedem Exit

    let lock = file.acquireLock();
    defer lock.release();            // läuft VOR file.close() (LIFO)

    return file.readAllText();
}
```

Bei Exception aus `readAllText`: erst `lock.release()`, dann `file.close()`, dann Exception propagiert weiter.

Lyric hat **kein** `finally` — `defer` deckt alle Use-Cases ab und ist allgemeiner.

---

## 19. Coroutinen

Coroutinen sind kooperatives Multitasking. Eine Coroutine pausiert sich mit `yield`, der Aufrufer setzt sie mit `resume` fort.

```lyr
import std.io.console;

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

fn main(): int {
    let fib = fibonacci();
    for (i in 0..10) {
        let v = resume fib;
        console.println(f"{v}");
    }
    return 0;
}
```

Die Regeln dazu:

- `resume co` ist ein **Ausdruck** (Präfix, bindet wie unäre Operatoren): er setzt die
  Coroutine fort und liefert den Wert des nächsten `yield`. Als Statement (`resume co;`)
  wird der Wert verworfen.
- `yield` ist nur in Funktionen mit Rückgabetyp `Coroutine<T>` erlaubt; der ge-yieldete
  Wert muss zu `T` passen. Nacktes `yield;` verlangt `Coroutine<void>`.
- Eine Coroutine endet mit nacktem `return;` (frühes Ende) oder wenn der Body
  durchläuft. `return wert;` gibt es in Coroutinen nicht — sie liefern Werte
  ausschließlich über `yield`. Weitere `resume`-Aufrufe nach dem Ende werfen
  `CoroutineEndedError`.

### 19.1 Bidirektional? Post-v1.

Werte **in** eine laufende Coroutine schicken (`resume co, wert` mit `yield` als
Ausdruck, der den Wert empfängt) gibt es in v1 nicht — das kommt, wenn überhaupt,
als Paket mit `Coroutine<TOut, TIn>` nach v1. Wer heute Daten in eine Coroutine
reichen will, nutzt geteilten Zustand über eine `class`-Referenz, die beide Seiten
kennen:

```lyr
class Inbox { message: string = "" }

fn worker(inbox: Inbox): Coroutine<string> {
    while (true) {
        if (inbox.message == "stop") { return; }
        yield f"gesehen: {inbox.message}";
    }
}
```

### 19.2 Game-Pattern: `waitSeconds`

Klassischer Unity-artiger Coroutine-Use-Case:

```lyr
fn explosion(): Coroutine<void> {
    showFlash();
    yield waitSeconds(0.1);
    playSound("boom");
    yield waitSeconds(0.5);
    spawnParticles();
    yield waitSeconds(2.0);
    cleanup();
}
```

`waitSeconds` wäre eine Host-bereitgestellte Funktion, die yieldet bis genug Zeit vergangen ist. Der Host driveld den Coroutine-Scheduler.

---

## 20. Capabilities und FFI

Lyric hat **keine direkte FFI im Source** (kein `@extern`, kein `[DllImport]`). Stattdessen: der Host registriert Bindings beim VM-Init, und die Stdlib ist in Capability-Stufen unterteilt.

### 20.1 Capability-Stufen

| Capability | Modul | Standard im Standalone | Standard im Embedded |
|---|---|---|---|
| (immer) | `std.core`, `std.option`, `std.error`, `std.string`, `std.fmt`, `std.math`, `std.collections`, `std.iter`, `std.coroutine`, `std.io.console` | ✓ | ✓ |
| `fileAccess` | `std.io.file` | ✓ | host-Entscheidung |
| `networkAccess` | `std.io.net` | ✓ | host-Entscheidung |
| `osAccess` | `std.os` | ✓ | host-Entscheidung |
| `hostAccess` | `std.dotnet` | ✓ | host-Entscheidung |

Wenn ein Modul versucht ein permission-gated Modul zu importieren ohne dass die Capability gewährt ist, gibt es einen Fehler `LYR-CAP0001 module 'std.io.file' requires capability 'fileAccess' which is not granted`.

### 20.2 Standalone-Modus

Wenn du `lyric run myapp.lyr` aufrufst, hat die VM-Instanz **alle** Capabilities. Du kannst freie Hand mit File-IO, Netzwerk, etc.

### 20.3 Embedded-Modus

Der Host kontrolliert. Siehe [§21](#21-embedding-für-hosts).

---

## 21. Embedding (für Hosts)

Wenn du Lyric in eine C#-Anwendung einbettest:

```csharp
using Lyric.Embedding;

// 1. VM erzeugen mit Capabilities
var vm = new LangVm(new Capabilities {
    FileAccess     = false,
    NetworkAccess  = false,
    OsAccess       = false,
    HostAccess     = false,
});

// 2. Host-Funktionen registrieren
vm.RegisterFunction("playSound", (string name) => {
    AudioEngine.Play(name);
});

vm.RegisterFunction<int, int, int>("damage", (entityId, amount) => {
    var entity = World.Get(entityId);
    return entity.TakeDamage(amount);
});

// 3. Host-Typen registrieren
vm.RegisterType<Vector3>(builder => {
    builder.Field("x", v => v.X);
    builder.Field("y", v => v.Y);
    builder.Field("z", v => v.Z);
    builder.Method("magnitude", v => v.Length());
});

// 4. Script laden und ausführen
var bytecode = vm.Compile(File.ReadAllText("mods/enemy.lyr"));
vm.Run(bytecode);

// 5. Funktionen aus dem Script aufrufen
var dmg = vm.Call<int>("calculateDamage", attackerId, defenderId);

// 6. Hot-Reload
File.WatcherEvent += (sender, args) => {
    vm.Reload("mods/enemy.lyr");
};
```

Im Script kann der Modder das nutzen wie normale Funktionen/Typen:

```lyr
// mods/enemy.lyr
pub fn calculateDamage(attackerId: int, defenderId: int): int {
    let attacker = world.get(attackerId);
    let defender = world.get(defenderId);
    let baseDmg = attacker.strength - defender.defense;
    playSound("hit");
    return baseDmg;
}
```

---

## 22. Standardbibliothek-Überblick

Vollständige API-Doku entsteht in v1.0. Hier nur ein Überblick.

> **`println` nimmt `string`.** Die gewollte Form ist `println<T :: [Display]>(v: T)` — eine
> Funktion für alle Typen. Sie setzt voraus, dass Builtin-Typen ein Interface erfüllen können, und
> das entsteht erst mit **M8**. Bis dahin gehen Zahlen über einen f-String: `println(f"{v}")`.
> Sobald die Builtin-Konformanz da ist, wird `println(v)` nachträglich legal; der Weg ist nicht
> verbaut.

| Modul | Inhalt | Capability |
|---|---|---|
| `std.core` | `panic`, `assert`, `todo`, `unreachable` | — |
| `std.option` | `Option<T>`, `unwrap`, `unwrapOr`, `map`, `flatten` | — |
| `std.error` | `Throwable`, `Exception`, gängige Errors | — |
| `std.string` | String-Manipulation | — |
| `std.fmt` | `format`, `Formattable` | — |
| `std.math` | `sqrt`, `pow`, `abs`, `sin`, `cos`, `pi`, `e` | — |
| `std.collections` | `List<T>`, `Map<K,V>`, `Set<T>` | — |
| `std.iter` | `Iterator<T>`, Standard-Impls | — |
| `std.coroutine` | `Coroutine<T>`, `yield`/`resume`-Primitives | — |
| `std.io.console` | `print`, `println`, `eprintln`, `readLine` | — |
| `std.io.file` | `readText`, `writeText`, `exists`, `delete` | `fileAccess` |
| `std.io.net` | basic HTTP, TCP | `networkAccess` |
| `std.os` | env, args, exit, process | `osAccess` |
| `std.dotnet` | .NET-Reflection-Brücke | `hostAccess` |

---

## 23. CLI-Befehle

| Befehl | Zweck |
|---|---|
| `lyric --version` | Compiler-Version |
| `lyric --help` | Hilfe |
| `lyric check <file>` | Resolve + Parse + Sema, nur Diagnosen |
| `lyric tokenize <file>` | Lexer-Debugausgabe |
| `lyric parse <file>` | AST-Dump |
| `lyric build <file> [-o <out>]` | Compile zu `.lyrbc` |
| `lyric disasm <file.lyrbc>` | Bytecode-Disassembly |
| `lyric run <file>` | Compile + Execute |
| `lyric run <file.lyrbc>` | Execute Bytecode direkt |
| `lyric test [dir]` | `@test`-Funktionen ausführen |
| `lyric repl` | Interaktive REPL |

---

## 24. Was kommt nach v1?

Folgende Features sind bewusst **post-v1** und in v1-Quellen weder vorhanden noch antizipiert:

- **DateTime, Regex, JSON-Parser** in der Stdlib (v1.1)
- **`pub(package)`-Sichtbarkeit** (v1.1)
- **Newtypes** mit nominaler Trennung (v1.1)
- **Raw-Strings** `r"..."` (v1.1)
- **LSP-Server** für Editor-Integration (v1.2)
- **Async/Await-Syntax** als Zucker über Coroutinen (v1.3)
- **User-defined Operator-Overloading** (v1.4)
- **Formatter** `lyric fmt` (v1.5)
- **JIT-Backend** für Performance (post-v1.5, falls Bedarf)
- **Package-Manager** (nur falls Community entsteht)

**Niemals** geplant:
- Class-Inheritance.
- `Result<T, E>` als zweiter Error-Mechanismus parallel zu Exceptions.
- `unsafe` / Raw-Pointer.
- Direkte FFI (`@extern`/`[DllImport]`) in Source.
- Eigener Step-Debugger.
- Eigene Package-Registry.
- Plugins für mehr als VS Code (community-Aufgabe).

Bis v1.0 ist die Sprache absichtlich klein gehalten, damit Compiler und VM stabil und korrekt sein können. Konkrete Sequenzierung in [`ROADMAP.md`](ROADMAP.md).
