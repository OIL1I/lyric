# Lyric – Roadmap und Design-Referenz

> Single Source of Truth für alle Design-Entscheidungen, Meilensteine und Architektur-Vereinbarungen der Sprache **Lyric** und ihres Compilers/VMs `lyric`. Bei Konflikt mit `Doku.md` gilt diese Datei für Architektur-/Plan-Themen, `Sprache.md` für die formelle Syntax.

## Inhaltsverzeichnis

1. [Projekt-Identität](#projekt-identität)
2. [Designprinzipien](#designprinzipien)
3. [Architektur](#architektur)
4. [Sprach-Eckdaten (kompakter Überblick)](#sprach-eckdaten)
5. [Meilensteine M0–M10](#meilensteine)
6. [Diagnostik-Code-Bereiche](#diagnostik-code-bereiche)
7. [Architecture Decision Records (ADRs)](#architecture-decision-records)
8. [Was nach v1.0 kommt (Skizze)](#was-nach-v10-kommt)

---

## Projekt-Identität

**Lyric** ist eine statisch typisierte, GC-managed Application-Sprache, die als standalone-Executable (CLI/Desktop-Apps) **und** als embedded Runtime in Hosts (Game-Engines, Editoren, Tools) läuft. Dieselbe Sprache, dieselbe Stdlib, dieselbe VM — der Host entscheidet, welche Capabilities das laufende Script bekommt.

**Vorbild-Mischung:**
- Sprach-DNA: C# + Swift + Rust (modern, ohne klassische Inheritance, mit Pattern-Match und Generics).
- Embedding-Modell: Lua + Wren (sandbox-fähig, kapabilitätenbasiert).
- Tooling-Strategie: Minimalismus. Was die Sprache nicht selbst braucht, delegiert sie an .NET-Ökosystem.

**Performance-Ziel**: gut genug für Game-Logik und CLI-Tools. **Nicht** AAA-Render-Loop-tauglich. Wer Native-Speed braucht, schreibt das in der Host-Sprache.

**Hauptmotiv des Projekts**: zwischen Lernen und Nutzen. Jeder Meilenstein muss für sich Wert haben — wenn das Projekt nach M5 pausiert, hat M0–M5 trotzdem etwas geliefert.

---

## Designprinzipien

Die vier Regeln, gegen die jede Designentscheidung geprüft wird:

1. **Eine Identität, keine zwei Sprachen.** Lyric ist eine Sprache mit zwei Delivery-Modes (standalone, embedded). Keine Spaltung in „Vollversion" und „Skript-Subset" wie Oil sie geplant hatte.
2. **Explizit über implizit, aber kompakt.** `mut fn` markiert Mutation, `pub` markiert Exports, `?T` markiert Optionalität. Keine versteckten Effekte. Aber: implizite Closures, implizite `this`, kein `rec self: ref Self`-Boilerplate.
3. **Ein Mechanismus pro Konzept.** Errors: nur Typed Exceptions, kein paralleles Result-System. Cleanup: nur `defer`, kein zusätzliches `finally`-Suffix. Memory: nur GC, kein hybrides Modell. Doppel-Mechanismen sind explizit verboten.
4. **Auslieferbarkeit vor Vollständigkeit.** Lieber kein Feature als ein halbes Feature. Lieber v1 später als ein v1, das niemand benutzen kann.

---

## Architektur

### Ordnerstruktur (geplant)

```text
lyric/
├─ Lyric.sln                              # .NET Solution
├─ src/
│  ├─ Lyric.Core/                         # Diagnostics, SourceManager, Span
│  ├─ Lyric.Lexing/                       # Tokenizer
│  ├─ Lyric.Parsing/                      # Recursive-descent + Pratt
│  ├─ Lyric.Ast/                          # AST-Typen, Dumper
│  ├─ Lyric.Resolver/                     # Module-Auflösung, Imports
│  ├─ Lyric.Sema/                         # Type-Checker, Generics-Monomorph
│  ├─ Lyric.Ir/                           # Typed Mid-IR
│  ├─ Lyric.Bytecode/                     # Bytecode-Format, Serializer
│  ├─ Lyric.Vm/                           # Interpreter, Value-Repr, GC-Hook
│  ├─ Lyric.Stdlib/                       # Stdlib-Module (z.T. nativ)
│  ├─ Lyric.Embedding/                    # Host-API (LangVm)
│  └─ Lyric.Cli/                          # CLI-Executable
├─ stdlib/                                # Stdlib-Source (.lyr-Dateien)
├─ tests/
│  ├─ Lyric.Tests.Lexing/                 # xUnit
│  ├─ Lyric.Tests.Parsing/
│  ├─ Lyric.Tests.Sema/
│  ├─ Lyric.Tests.Vm/
│  └─ Lyric.Tests.E2E/                    # ganze Programme kompilieren + ausführen
├─ examples/
│  ├─ hello.lyr
│  ├─ fizzbuzz.lyr
│  └─ embedded-host/                      # Beispiel-C#-Host
├─ tooling/
│  ├─ vscode-lyric/                       # TextMate-Grammar + Minimal-Extension
│  └─ langserver/                         # (post-v1)
└─ docs/
   ├─ Sprache.md
   ├─ ROADMAP.md
   └─ Doku.md
```

### Pipeline

```text
.lyr-Source
   │
   ▼
[Lexer]       → Token-Stream
   │
   ▼
[Parser]      → AST
   │
   ▼
[Resolver]    → Symbol-Tabelle pro Modul
   │
   ▼
[Sema]        → typed AST (mit Generics-Monomorphisierung)
   │
   ▼
[IR-Lowering] → Typed Mid-IR (SSA-light)
   │
   ▼
[Bytecode]    → .lyrbc-File (oder in-memory)
   │
   ▼
[VM]          → Ausführung
```

Jede Stufe ist als **Library** in der eigenen Assembly. CLI ist ein dünner Wrapper. Embedding-API (`Lyric.Embedding.LangVm`) kapselt Pipeline-Stages, damit Hosts wahlweise Source-/Bytecode-Eingabe nutzen können.

### Zentrale Komponenten

- **`Lyric.Core.SourceManager`** — lädt Files, vergibt `FileId`, mappt Offset → (Zeile, Spalte).
- **`Lyric.Core.DiagnosticEngine`** — sammelt, sortiert deterministisch, rendert Text/JSON.
- **`Lyric.Vm.LangVm`** — Embedding-Hauptklasse. RegisterFunction, RegisterType, RegisterCapability, RunScript, Compile, Reload, Call.
- **`Lyric.Bytecode.Module`** — Bytecode-Container mit Header (Magic + Version + Capabilities-Bitset + Type-Table + Function-Table + Constant-Pool).

---

## Sprach-Eckdaten

Kompakter Überblick. Vollständige Spec in [`Sprache.md`](Sprache.md), User-Erklärung in [`Doku.md`](Doku.md).

| Achse | Entscheidung |
|---|---|
| **Paradigma** | Statisch typisiert, GC-managed, Compile to Bytecode + VM-Interpreter |
| **Implementation** | C# / .NET 9+ |
| **Datei-Endung** | `.lyr` (Source), `.lyrbc` (Bytecode) |
| **Compiler-Binary** | `lyric` |
| **Typsystem** | Lokal-Inferenz, Generics (Monomorphisierung), keine Inheritance |
| **Daten** | `struct` (Value) + `class` (Reference), `enum` mit Payload |
| **Polymorphie** | `interface` mit Default-Methoden, `::` als „implements"-Operator |
| **Numerik** | `int`=i64, `uint`=u64, `float`=f64 als Default; plus `int8..int64`, `uint8..uint64`, `float32`/`float64` |
| **Nullable** | `?T` = `Option<T>`, `?.`, `??`, `!` |
| **Mutability** | `let` (default) / `var`; Methoden default immutable, `mut fn` für Mutation |
| **Strings** | UTF-8 Fat-Pointer, immutable, f-Strings `f"hello {name}"` v1-Pflicht |
| **Errors** | Typed Exceptions mit `throws`, plus `panic` (nicht-catchable) |
| **Cleanup** | nur `defer`, kein `finally` |
| **Concurrency** | Single-threaded VM, Coroutinen mit `yield`/`resume` |
| **Closures** | implizites Capture |
| **Module** | eine Datei = ein Modul, Pfad-Inferenz aus Verzeichnis, `import`-Statement |
| **Sichtbarkeit** | `pub` oder modul-privat (v1, mehr in v1.1) |
| **Extensions** | `extend Type { ... }` ohne Aktivierungs-Direktive |
| **FFI** | Host-controlled via Capabilities + RegisterFunction/Type. Kein `@extern` in Source. |

---

## Meilensteine

Jeder Meilenstein hat **ein klar definiertes Exit-Kriterium** und **ein auslieferbares Artefakt**. Zeit-Schätzungen sind für ~10 h/Woche solo.

### M0 — Setup (1–2 Wochen)

**Ziel**: Repo, Solution, CI, leeres CLI.

**Lieferposten**:
- .NET-Solution mit Assemblies: `Lyric.Core`, `Lyric.Cli`.
- xUnit-Testprojekt-Skelett.
- GitHub Actions CI: `dotnet build` + `dotnet test` auf jedem Push.
- `Lyric.Core.SourceManager` + `DiagnosticEngine` mit `Span`-Typ, deterministischer Sortierung, Text- und JSON-Output.
- CLI: `lyric --version`, `lyric --help`.

**Exit**: `dotnet build` läuft grün, CI ist grün, `lyric --version` druckt etwas.

### M1 — Lexer (2–3 Wochen)

**Ziel**: vollständiger Tokenizer.

**Lieferposten**:
- Alle Token-Klassen aus `Sprache.md` §1.
- Keywords, Operatoren (Longest-Match), Literals (int mit Suffixen/Präfixen/Separatoren, float, string, char, bool, null).
- f-String-Lexing (Sub-Token-Modi: String-Part, Interpolation-Start, Embedded-Expression, Interpolation-End).
- Verschachtelbare Block-Kommentare, Line-Kommentare, Doc-Kommentare (tokenisiert, Semantik post-v1).
- Diagnostik-Codes `LYR-LEX0001..0020`.
- CLI: `lyric tokenize <file>` zeigt Token-Stream (Debug-Mode).
- Golden-Tests für alle Token-Klassen (positiv + negativ).

**Exit**: `examples/hello.lyr` tokenisiert ohne Fehler. ~25+ Lexer-Tests grün.

### M2 — Parser (5–7 Wochen)

**Ziel**: Recursive-Descent + Pratt für Expressions. AST komplett.

**Lieferposten**:
- AST-Typen für alle Knoten aus `Sprache.md` (Decls, Stmts, Exprs, Patterns).
- AST-Dumper für Test-Snapshots.
- Parser:
  - Top-Level: `module`-Header, `import`, `pub`, `fn`, `struct`, `class`, `enum`, `interface`, `extend`, `type` (Alias), globale `let`.
  - Statements: alle aus `Sprache.md` §5.
  - Expressions: Pratt-Parser für alle Operatoren mit korrekter Präzedenz (siehe §6.1).
  - Patterns: alle Formen inkl. Or-Patterns, Range, Guards.
  - Struct-Init, Array-Lit, Tuple-Lit, Lambda.
- Generics-Syntax: `<T>`, `<T :: [I1, I2]>`.
- Diagnostik-Codes `LYR-PAR0001..0050`.
- CLI: `lyric parse <file>` zeigt AST-Dump.
- Golden-Tests für jede Syntax-Form.

**Exit**: `examples/hello.lyr`, `fizzbuzz.lyr` und ein paar weitere parsen sauber. Alle Präzedenz-Fixtures grün.

### M3 — Resolver + Sema (basic) (5–7 Wochen)

**Ziel**: Module-Auflösung, Symboltabellen, Typsystem **ohne** Generics.

**Lieferposten**:
- `Lyric.Resolver.ModuleResolver`: Datei → Modul-Mapping, `import`-Resolution, Pfad-Validierung, Zyklus-Detection.
- Symbol-Tabellen pro Modul mit Visibility-Check (`pub`).
- `Lyric.Sema.TypeChecker`:
  - Primitives mit Größen/Signedness.
  - Benannte Typen (Struct/Class/Enum/Interface — non-generic).
  - `?T`, `T[]`, `T[N]`, Tupel.
  - Lokale Typ-Inferenz aus Initializer.
  - Definite-Assignment-Analyse für `var`.
  - Cast-Regeln (`as`).
  - Interface-Konformitäts-Check für `::`.
  - Funktions-Signaturen, Default-Params, Return-Path-Coverage.
  - `main`-Entry-Contract.
- Diagnostik-Codes `LYR-RES0001..0020`, `LYR-SEM0001..0060`.
- CLI: `lyric check <file>` validiert ohne Build.

**Exit**: Programme ohne Generics und ohne Pattern-Match-Payload-Destructuring werden vollständig typgeprüft. E2E-Test: 10+ kleine Programme grün.

### M4 — Sema (full) (4–6 Wochen)

**Ziel**: Generics + Pattern-Match-Vollausbau + Coroutine-Sema + Exception-Sema.

**Lieferposten**:
- Generics: Type-Parameter, Constraints (`<T :: [I]>`), Monomorphisierung.
- Pattern-Match:
  - Volle Pattern-Grammar (Literale, Wildcards, Bindings, Tuple-Destructuring, Struct-Destructuring, Enum-Variant-Destructuring, Or-Patterns, Range, Guards).
  - Exhaustivity-Check mit konkreter Fehlermeldung welche Cases fehlen.
- Interfaces mit Default-Methoden: Resolution-Reihenfolge (`impl` → Default → Fehler).
- Coroutine-Sema: `yield`/`resume`-Validation, Coroutine-Return-Typ-Inferenz.
- Exception-Sema: `throws`-Propagation, Catch-Type-Validation, Throwable-Constraint.
- `extend`-Blöcke mit Orphan-Rule-Check.
- Closure-Sema: Capture-Validierung, Lambda-Typ-Inferenz.
- Diagnostik-Codes `LYR-SEM0060..0150`.

**Exit**: Volle v1-Sprache typgeprüft. Komplexe Programme (mit Generics-Containern, Pattern-Match, Coroutinen) compilen sauber.

### M5 — IR + Bytecode (3–5 Wochen)

**Ziel**: Bytecode-Format design + Lowering.

**Lieferposten**:
- `Lyric.Ir`-Typen: Module, Function, BasicBlock, Inst.
- IR-Instructions: Const, BinOp, UnOp, Call, Return, Branch, CondBranch, Phi, LoadField, StoreField, NewStruct, NewClass, NewArray, MatchDispatch, Throw, Catch, Yield, Resume.
- Lowering AST → IR (mit Closure-Lifting, Coroutine-State-Machine-Lowering).
- Bytecode-Format-Spec (stack-based VM): Opcode-Liste, Operand-Encoding (LEB128), Header (Magic, Version, Capabilities-Bitset, Type-Table, Function-Table, Constant-Pool) — als normatives Dokument [`docs/Bytecode.md`](Bytecode.md) (ADR-013). ✅
- Bytecode-Serializer (`.lyrbc`-Files).
- Bytecode-Disassembler (`lyric disasm <file.lyrbc>`).
- Diagnostik-Codes `LYR-IR0001..0010`, `LYR-BC0001..0010`.
- CLI: `lyric lower <file>` (IR-Dump, Debug), `lyric build <file>` produziert `.lyrbc`.
- **Gate-Programm `examples/arith.lyr`** — stdlib-frei, damit es allein aus M5-Mitteln compiliert.

**Exit**: `examples/arith.lyr` compiliert zu Bytecode, Disasm zeigt sinnvolle Instruktionen. **v0.1 Release-Tag**.

> **Nachtrag (2026-08-02)**: Von den oben gelisteten IR-Instruktionen wurde nur die skalare Hälfte
> gebaut; `LoadField`, `StoreField`, `NewStruct`, `NewClass`, `NewArray`, `MatchDispatch`, `Throw`,
> `Catch`, `Yield`, `Resume` sowie Closure-Lifting und Coroutine-Lowering stehen aus und liegen
> jetzt in **M7**. Begründung und Konsequenzen: Korrektur bei M7.
>
> `Phi` ist die eine Ausnahme: es wurde nicht vergessen, sondern **bewusst gestrichen**. Werte über
> Blockgrenzen laufen im P4-Lowering durch (ggf. synthetische) Locals, nicht durch Temps — damit
> braucht diese IR keine Phi-Knoten. Die Entscheidung trägt auch den Verifier (Single-Definition
> pro Temp macht den Availability-Dataflow äquivalent zur Dominanz) und den Stack-Scheduler
> (Stack an jeder Blockgrenze leer). Sie ist nicht nachzuholen, sondern zu erhalten.

> **Korrektur (2026-07-30, während M5/P4; ratifiziert im Scope-Check 2026-08-02):** Das
> Exit-Kriterium lautete ursprünglich „Hello-World
> compiliert zu Bytecode". Das war aus M5s eigenen Lieferposten nie erreichbar: `examples/hello.lyr`
> ruft `console.println` und nutzt f-Strings, braucht also eine Import-Tabelle **mit Signaturen** —
> und die entsteht erst mit dem Stdlib-Minimum in **M6** (`ExternalSymbol` trägt heute nur Name und
> Modulpfad, keine Signatur). Das Kriterium ist deshalb auf ein stdlib-freies Programm umgestellt.
>
> Keine Scope-Kürzung, sondern die Auflösung einer Inkonsistenz zwischen zwei Meilensteinen:
> Hello-World **ist bereits M6s Exit-Kriterium**, und M6 liefert mit `std.io.console.println` genau
> das, was dafür fehlt. M5 endet damit an der Grenze, die es selbst kontrolliert.

### M6 — VM (basic) (4–6 Wochen)

**Ziel**: Bytecode-Interpreter, einfache Programme laufen.

**Lieferposten**:
- Value-Repräsentation (tagged union mit boxed reference types für `class`).
- Stack-VM mit Operand-Stack und Call-Frame-Stack.
- Implementierung aller Opcodes außer Exceptions und Coroutinen.
- GC-Strategie: nutzt .NET GC (Werte sind .NET-Objekte, .NET kümmert sich um Tracing).
- Stdlib-Minimum: `std.io.console.println` (nimmt `string`), `std.core.panic`, plus die
  compiler-internen f-String-Helfer (`concat`, `toString`).
- Source-Mapping: Bytecode-PC → (FileId, Line, Col) für Runtime-Errors.
- Diagnostik-Codes `LYR-VM0001..0020`.
- CLI: `lyric run <file>` (Compile + Execute in einem Schritt).

**Exit**: Hello-World läuft. E2E-Tests: 15+ einfache Programme. *(Korrektur unten.)*

> **Korrektur (2026-08-02, vor M6/Slice 1):** `std.fmt.format` ist nach **M8** verschoben. Keines
> der drei Exit-Programme benutzt Format-Specs (`{x:N2}` steht nur in `shapes.lyr`/`stats.lyr`) —
> eine .NET-kompatible Formatierungs-Engine mit Padding, Rundung und Spec-Grammatik ist ein
> eigener Brocken und gehört dorthin, wo die Stdlib ohnehin ausgebaut wird. f-Strings ohne Spec
> lowern zu einer `concat`/`toString`-Kette und brauchen kein `format`.
>
> Ebenfalls hier fixiert: **`println` nimmt `string`**. Die gewollte Form
> `println<T :: [Display]>(v: T)` setzt Builtin-Konformanz voraus, und die entsteht erst in **M8**.
> Zahlen gehen bis dahin über `println(f"{v}")`; die Beispiele in `Sprache.md` §8 und `Doku.md` §19
> sind entsprechend korrigiert.
>
> *(Zur Ratifizierung im nächsten Scope-Check.)*
>
> **Richtigstellung (2026-08-02):** Der ursprüngliche Text begründete das mit „Lyric hat kein
> Overloading". Das war frei erfunden — es gab dazu nie eine Entscheidung, und jede andere
> Erwähnung von Overloading in der Spec meint das **Operator**-Overloading. Die tatsächliche
> Begründung ist die fehlende Builtin-Konformanz, wie oben jetzt formuliert. Zum Thema selbst
> siehe **ADR-015**.

> **Korrektur (2026-08-02, Abschluss M6/Slice 2):** Das Exit-Kriterium ist **`examples/hello.lyr`**;
> FizzBuzz und Fibonacci entfallen hier. Beide sind aus M6s eigenen Lieferposten nicht erreichbar:
> FizzBuzz braucht `for-in` über einen Range, also den `Iterator`-Kontrakt, und Fibonacci ist als
> `Coroutine<int>` geschrieben — Coroutinen sind ausdrücklich **M7**. Der Stdlib-Teil, den M6 zu
> liefern hat, steht dagegen vollständig: `println` samt Import-Bindung mit Signaturen und f-Strings.
>
> Das ist derselbe Fehler wie bei M5s Exit, eine Stufe später: ein Meilenstein wurde an einem
> Programm gemessen, dessen Sprachmittel erst der nächste liefert. Die beiden Programme wandern
> nach M7 (Fibonacci) bzw. dorthin, wo `for-in` gelowert wird (FizzBuzz) — sie bleiben Gate, nur
> nicht hier. Die E2E-Forderung („15+ einfache Programme") bleibt und ist mit 44 Pipeline-Tests
> übererfüllt.
>
> *(Zur Ratifizierung im nächsten Scope-Check.)*

### M7 — Objektmodell + VM (full) (14–20 Wochen)

**Ziel**: Alles, was kein Skalar ist. Objekte, Arrays, Interfaces, Exceptions, Closures,
Coroutinen, Generics — vom IR über das Bytecode-Format bis in die VM.

**Lieferposten** (Slices in Abhängigkeitsreihenfolge, jeder mit eigenem Gate-Programm):

| Slice | Inhalt | Gate |
|---|---|---|
| P1 ✅ | Classes: Types-Sektion (Id 3), Heap-Objekte, Felder (**ohne** Methoden, s. u.) | `examples/objects.lyr` |
| P1b ✅ | `static`/`static let` (ADR-014), Methoden-Lowering mit Empfänger als Parameter 0 | `examples/objects.lyr` |
| P2 ✅ | Arrays: Literal, `[x]*n`, `xs+ys`, Index, `length` (ADR-016) | `examples/arrays.lyr` |
| P2b ✅ | Optionals (`?T`, `??`, `!`, Flow-Narrowing) | `examples/optionals.lyr` |
| P3b | Enums (Unit-/Tuple-/Struct-Varianten) + `match` | `examples/shapes.lyr` ohne `for-in`/`std.math` |
| P3 | Interfaces + vtable-Dispatch (**nach** P3b) | eigenes Programm mit Interface |
| P4 | Structs (Wert-Semantik, Copy-on-Assign) | `examples/bank.lyr` |
| P5 | Exceptions + `defer` (LIFO auf jedem Exit-Pfad) | — |
| P6 | Closures (Lifting + Environment-Objekt) | `examples/inventory.lyr` |
| P7 | Coroutinen (State-Machine-Lowering, ADR-006) | `examples/fibonacci.lyr` |
| P8 | Generics-Monomorphisierung + `for-in`/`Iterator` | `examples/fizzbuzz.lyr`, `examples/stats.lyr` |

- IR-Instruktionen: `NewClass`, `NewStruct`, `NewArray`, `LoadField`, `StoreField`, `LoadElem`,
  `StoreElem`, `ArrayLen`, `CallVirt`, `Throw`, `Catch`, `Yield`, `Resume` — aus M5s Liste
  nachgeholt (siehe Korrektur unten).
- Bytecode-Format **1.2**: Types-Sektion (Id 3) wird geschrieben, zusammengesetzte Typ-Tags ab
  `0x40`. Beides ist in `docs/Bytecode.md` bereits reserviert; kein Formatbruch nötig.
- Diagnostik-Codes `LYR-VM0020..0050`.

**Exit**: Alle `examples/*.lyr` laufen. **v0.5 Release-Tag**.

> **Korrektur (2026-08-02, vor P2):** Zwei Ergänzungen an der Slice-Tabelle, beide aus derselben
> Prüfung — was verlangt das Gate-Programm wirklich?
>
> **Optionals bekommen einen eigenen Slice (P2b).** Sie fehlten in der ursprünglichen Tabelle
> komplett, obwohl `?T` Kernsprache ist (§7, mit `??`, `!` und Flow-Narrowing) und `stack.lyr`,
> `inventory.lyr` und `stats.lyr` alle daran hängen. Ohne eigenen Slice wären sie bis P8 blockiert
> gewesen, ohne dass irgendwo stünde, warum.
>
> **P2s Gate ist nicht `stats.lyr`.** Das Programm braucht neben Arrays noch `params`-Variadics,
> `for-in` (also `Iterator`, P8), Optionals (P2b) und eine Format-Spec (`std.fmt`, M8) — vier
> Dinge aus vier verschiedenen Meilensteinen. Es wandert zu P8, wo das letzte davon fällt; P2
> bekommt ein Programm, das nur aus P2-Mitteln besteht.
>
> **Offen und noch nicht entschieden**: ob `T[]` ein eingebauter Typ mit Intrinsics ist oder
> tatsächlich Zucker für eine generische Stdlib-Klasse `List<T>`, wie `Doku.md` §5.2 es beschreibt.
> P2 implementiert `.length`/`.push`/`.pop` als **Intrinsics auf dem eingebauten Typ** — die
> dokumentierte Oberfläche stimmt, der Unterbau ist der einfachere. Eine echte `List<T>` bräuchte
> Generics (P8) und `std.collections` (M8) und wäre auf sich selbst gebaut.

> **Korrektur (2026-08-02, vor P3):** Enums und `match` bekommen einen eigenen Slice (**P3b**), und
> er läuft **vor** P3. Sie fehlten in der Tabelle komplett — dieselbe Lücke wie zuvor bei den
> Optionals. Beides ist Kernsprache (§3.4, §6.2 samt Exhaustivitäts-Prüfung `LYR-SEM0050`, die die
> Sema längst beherrscht) und beides ist teuer: Varianten brauchen ein Tag im Wert, `match` braucht
> Pattern-Dekomposition und einen Dispatch.
>
> Aufgefallen ist es wieder am Gate: `examples/shapes.lyr` war P3 (Interfaces) zugeordnet und
> **enthält kein einziges Interface** — es ist ein Enum-Programm mit `match`. Es wandert zu P3b;
> P3 bekommt ein Programm, das tatsächlich über einen Interface-Typ dispatcht. Interfaces selbst
> sind in der Sema fertig (Konformanz, Default-Methoden, `extend`, Orphan-Rule), P3 ist deshalb
> reiner Dispatch: vtable in der Types-Sektion und ein `callvirt`.

> **Offene Sprachfrage (2026-08-02, aus P1) — blockiert P3:** Methoden sind in P1 bewusst **nicht**
> gelowert, und der Grund ist eine Lücke in `Sprache.md`, keine Zeitfrage. Die Grammatik kennt kein
> `static`; `this` ist in jedem Methodenrumpf erlaubt; und nichts legt fest, wie
> `Account.new(...)` — die Fabrik-Konvention aus `examples/bank.lyr`, ohne Empfänger — von
> `acc.deposit(...)` mit Empfänger unterschieden wird. Beide binden an dasselbe `FunctionSymbol`,
> und das kann nicht zugleich mit und ohne `this`-Parameter gelowert werden.
>
> Zu entscheiden **bevor P3** (Interface-Dispatch) beginnt: dort wird der Empfänger Parameter 0,
> und die vtable-Form hängt daran. Mögliche Richtungen: ein `static`-Marker in der Grammatik; oder
> „Methode ohne Empfänger" aus der Signatur ableiten (Rückgabetyp = eigener Typ ⇒ Fabrik) — was
> fragil ist; oder Fabriken gar nicht als Member führen, sondern als freie Funktionen im Modul.

> **Korrektur (2026-08-02, nach M6):** M7 hieß „VM (full)" und war mit **4–6 Wochen** veranschlagt
> für Exceptions, Coroutinen, Closures und Generics-Runtime. Diese Schätzung setzte ein
> Objektmodell voraus, das es nicht gibt — und der Grund dafür ist, dass **M5 und M6 je einen Teil
> ihrer eigenen Lieferposten nicht geliefert haben**, ohne dass es vermerkt wurde:
>
> - M5 listet die IR-Instruktionen `LoadField, StoreField, NewStruct, NewClass, NewArray,
>   MatchDispatch, Throw, Catch, Yield, Resume` sowie „Lowering AST → IR (mit **Closure-Lifting,
>   Coroutine-State-Machine-Lowering**)". Gebaut wurde davon nichts: das IR-Typ-Universum ist bis
>   heute `IrScalarType` und sonst nichts.
> - M6 listet „Value-Repräsentation (tagged union mit **boxed reference types für `class`**)".
>   Geliefert wurde die skalare Hälfte.
> - **ADR-006 §Konsequenz** weist das Coroutine-Lowering ausdrücklich M5 zu und lässt M7 „nur noch
>   die Runtime-Seite" — M7s Coroutinen-Posten steht damit auf einer Voraussetzung, die fehlt.
>
> Alle vier ursprünglichen M7-Themen brauchen Objekte: eine Closure **ist** ein
> Environment-Objekt, eine Coroutine ist laut ADR-006 ein Struct mit `step`-Methode, ein
> Exception-Wert ist eine Klasseninstanz, generische Container sind Klassen mit Arrays darin.
> Keines davon ist vor dem Objektmodell baubar. M7 nimmt die fehlenden Posten deshalb auf und die
> Schätzung steigt entsprechend.
>
> Warum das nicht auffiel: die Lücke tarnt sich als saubere Diagnose. `examples/bank.lyr` meldet
> `LYR-IR0001: type 'Account' is not supported by this compiler version yet` — ordentlich
> gerendert, mit Position, und liest sich wie eine geplante Grenze. Es ist aber ein offener
> Lieferposten. **Konsequenz für die Arbeitsweise**: `LYR-IR0001` heißt „noch nicht gebaut" und
> sagt nichts darüber, ob das so vorgesehen war. Am Ende jedes Meilensteins ist die Lieferposten-
> Liste Punkt für Punkt abzuhaken, nicht das Exit-Kriterium allein.
>
> **Kein Scope-Zuwachs**: es kommt nichts hinzu, was nicht schon in M5, M6 oder M7 stand. Die
> Arbeit wandert nur dorthin, wo sie tatsächlich getan wird. `docs/IDEAS.md` bleibt unberührt.
>
> Warum **ein** großer Meilenstein statt zweier: die acht Slices liefern je ein eigenes
> Gate-Programm, CONTRIBUTING Rule 3 („jeder Meilenstein liefert ein Artefakt") ist damit auf
> Slice-Ebene erfüllt. Eine Meilenstein-Grenze mitten durch das Objektmodell zu ziehen, würde die
> Nummerierung von M8–M10 verschieben, ohne an der Arbeit etwas zu ändern.
>
> *(Zur Ratifizierung im nächsten Scope-Check.)*

### M8 — Stdlib (4–6 Wochen)

**Ziel**: Alle 14 Stdlib-Module produktiv.

**Lieferposten**:
- `std.core`, `std.option`, `std.error`, `std.string`, `std.fmt`, `std.math`, `std.collections`, `std.iter`, `std.coroutine`, `std.io.console`, `std.io.file`, `std.io.net` (minimal HTTP/TCP), `std.os`, `std.dotnet`.
- Native-Hooks für Performance-kritische Operationen (z.B. `List<T>` direkt auf `System.Collections.Generic.List<>`).
- Capability-Enforcement: Imports von permission-gated Modulen prüfen Capabilities zur Resolve-Zeit.
- Diagnostik-Codes `LYR-CAP0001..0010`.
- Pro Modul: 10+ Unit-Tests.

**Exit**: Stdlib-Tests grün. Beispiel-CLI-Tool nutzbar (z.B. ein simples `wc`-Klon).

### M9 — REPL + Tests + Tooling (2–3 Wochen)

**Ziel**: User-Experience rund.

**Lieferposten**:
- `lyric repl`: interaktive REPL mit Persistent-Environment.
- `lyric test [dir]`: sammelt `@test`-Funktionen, führt sie aus, Output Text oder JSON.
- TextMate-Grammar für VS Code (Syntax-Highlighting): `tooling/vscode-lyric/syntaxes/lyric.tmLanguage.json`.
- Minimale VS-Code-Extension (Highlighting + Run-Command).
- README.md, LICENSE, CONTRIBUTING.md.
- Examples-Verzeichnis aufgefüllt.

**Exit**: `lyric repl` produktiv. `lyric test` läuft auf Stdlib. **v0.9 Release-Tag**.

### M10 — Embedding-API (2–3 Wochen)

**Ziel**: Game-Engine-/Tool-Embedding.

**Lieferposten**:
- `Lyric.Embedding.LangVm`-Klasse:
  - Konstruktor mit `Capabilities`-Object.
  - `RegisterFunction(name, Delegate)`.
  - `RegisterType<T>(name, configurator)` mit Field-/Method-Builder.
  - `Compile(string source)` → Bytecode-Handle.
  - `Run(Bytecode)` / `RunScript(path)`.
  - `Call<TReturn>(string functionName, params object[] args)`.
  - `Reload(path)` für Hot-Reload.
- Bidirektionale Marshalling-Schicht: Lyric-Werte ↔ .NET-Objekte.
- Beispiel: `examples/embedded-host/` zeigt simplen C#-Host, der ein Lyric-Script ausführt und Funktionen aufruft.
- Dokumentation der API in `Doku.md` §Embedding.

**Exit**: Beispiel-Host läuft. **v1.0 Release**.

### v1.0 — Release

Alle M0–M10 abgeschlossen. GitHub-Release mit Changelog, Binary für Windows/Linux/macOS (via `dotnet publish -r ...`), Doku-Site (statisches HTML aus den Docs generiert).

---

## Diagnostik-Code-Bereiche

Stabile Präfixe (vollständige Kataloge in den jeweiligen Modulen):

| Präfix | Bedeutung | Eingeführt in |
|---|---|---|
| `LYR-LEX####` | Lexer | M1 |
| `LYR-PAR####` | Parser | M2 |
| `LYR-RES####` | Resolver | M3 |
| `LYR-SEM####` | Semantik | M3+M4 |
| `LYR-IR####` | IR-Lowering | M5 |
| `LYR-BC####` | Bytecode | M5 |
| `LYR-VM####` | Runtime | M6+M7 |
| `LYR-CAP####` | Capabilities | M8 |
| `LYR-CLI####` | CLI/Build | M0 |

---

## Architecture Decision Records

Kompakte Liste der zentralen Designentscheidungen. Bei Konflikt mit der ROADMAP-Beschreibung oben hat das ADR Vorrang.

### ADR-001 — Bytecode-VM, kein AOT-Native in v1

**Datum**: 2026-06-05. **Status**: Akzeptiert.

**Entscheidung**: Lyric kompiliert zu Bytecode, der von einer Interpreter-VM ausgeführt wird. Kein AOT-Native-Compile (LLVM/Cranelift) in v1.

**Begründung**: VM ist Pflicht für Embedding (Hot-Reload, Sandbox). AOT zusätzlich wäre 2x Backend-Arbeit. JIT kann post-v1 als zweiter Backend hinter `IBackend`-artige Abstraktion kommen.

**Konsequenz**: Performance gut genug für Game-Logik und CLI, nicht für AAA-Render-Loops. Wer Native-Speed braucht, schreibt es in .NET-Host.

---

### ADR-002 — Implementation in C#/.NET

**Datum**: 2026-06-05. **Status**: Akzeptiert.

**Entscheidung**: Compiler und VM sind in C# implementiert. .NET 9+ als Baseline.

**Begründung**: User-Stärke ist C#/.NET. .NET hat einen ausgereiften GC, exzellentes Tooling, NuGet-Ökosystem. Embedding in .NET-Hosts (Unity, Godot, eigene C#-Apps) ist trivial.

**Konsequenz**: Cross-Platform via .NET self-contained-Builds. Performance der VM hängt am .NET-JIT — gut, aber nicht so schnell wie eine C/Rust-VM. Akzeptabel.

---

### ADR-003 — Keine klassische Inheritance, struct + class + interface

**Datum**: 2026-06-05. **Status**: Akzeptiert.

**Entscheidung**: Lyric hat keine `extends`/`:`-Inheritance. Code-Reuse läuft über Composition, Interface-Default-Methoden, `extend`-Blöcke. `struct` (Value) und `class` (Reference) unterscheiden sich nur in Semantik, nicht in Vererbung — beide ohne Subclassing.

**Begründung**: Klassische Inheritance ist seit ~15 Jahren als Anti-Pattern bekannt (Diamond, Fragile Base Class). Moderne Sprachen (Rust, Go, neuere Swift-Praxis) verzichten oder bewegen sich weg. `struct`+`class`-Distinktion ist nützlich für Game-Math (Vector3 = struct, Player = class).

**Konsequenz**: Vertrauter C#-Entwickler vermisst möglicherweise Properties (`get`/`set`) und Inheritance. Properties können in einer post-v1-Phase als Zucker für Methoden kommen, Inheritance nie.

---

### ADR-004 — `::` als „implements"-Operator

**Datum**: 2026-06-05. **Status**: Akzeptiert.

**Entscheidung**: `struct X :: [I1, I2] { ... }` deklariert Interface-Konformität. `:` bleibt für Typ-Annotationen (`let x: int`). `::` taucht **nicht** in Modul-Pfaden auf (das ist `.`).

**Begründung**: `:` ist überladen, wenn es sowohl Typ-Annotation als auch Interface-Konformität markiert. `::` löst das visuell und parser-seitig sauber. `[ ]` macht klar, dass es sich um eine Liste handelt.

**Konsequenz**: Nicht-Standard-Syntax (kein anderes weit verbreitetes Sprach-Idiom). Lern-Overhead für neue User, aber konsistent erklärbar.

---

### ADR-005 — Naming-Konvention erzwungen via Linter

**Datum**: 2026-06-05. **Status**: Akzeptiert für v1, Enforcement deferred.

**Entscheidung**: Typen `PascalCase`, alles andere `camelCase`. Konvention ist verbindlich, wird aber in v1 noch nicht vom Compiler erzwungen (kein Linter in v1). Stdlib-Code hält sich vorbildlich daran.

**Begründung**: Modern (Swift/Kotlin/Go). Für C#-User minimal anders (Methoden lowercase statt PascalCase) aber weniger laut.

---

### ADR-006 — Coroutine-Implementation: State-Machine-Lowering

**Datum**: 2026-06-05, entschieden 2026-07-23. **Status**: Akzeptiert (Option a).

**Entscheidung**: Coroutinen werden im IR zu State-Machines gelowert (wie C#-async/Rust-async): Sema/Lowering transformiert die Coroutine in ein Struct mit `step`-Methode und State-Variable. Keine Fibern via .NET-Threads.

**Begründung**: Ursprünglich offen bis M7 (a: State-Machine-Lowering, b: Fibern). Vorgezogen, weil das Bytecode-Format von der Wahl abhängt und in M5 designt wird: mit (a) braucht `.lyrbc` keine Coroutine-Opcodes, mit (b) müsste jede Runtime die Coroutine-Mechanik selbst mitbringen. (a) ist portabel (siehe ADR-013), GC-freundlicher und hält die VM einfach; die Komplexität liegt einmalig im Lowering statt in jeder Runtime.

**Konsequenz**: Das Coroutine-Lowering ist Teil von M5 (war dort bereits als Lieferposten gelistet); M7 implementiert nur noch die Runtime-Seite der gelowerten Form. Der Fiber-Ansatz ist verworfen.

> **Nachtrag (2026-08-02)**: Die *Entscheidung* — State-Machine statt Fibern — steht unverändert und hat sich bewährt: `.lyrbc` hat bis heute keine Coroutine-Opcodes, genau wie beabsichtigt. Die *Zuordnung* stimmt nicht mehr: M5 hat das Coroutine-Lowering nicht gebaut, es liegt jetzt in M7/P7 (siehe Korrektur bei M7). Damit fällt auch die Annahme, M7 sei „nur noch die Runtime-Seite" — die Runtime-Seite ist tatsächlich klein, das Lowering ist es nicht.

---

### ADR-007 — Capability-basiertes Stdlib-Modell

**Datum**: 2026-06-05. **Status**: Akzeptiert.

**Entscheidung**: Stdlib-Module sind in Permission-Stufen gruppiert. Host konfiguriert beim VM-Init, welche Permissions die VM-Instanz hat. Importe von permission-gated Modulen werden zur Resolve-Zeit gegen Capabilities geprüft.

**Begründung**: Unity/Godot bieten faktisch keine Sandbox — wir machen es besser. Trennt Trust-Boundary zwischen Host und Script sauber. App-Use-Case bekommt volle Permissions, Mod-Use-Case ist sandboxed.

**Konsequenz**: Standalone-CLI-Mode aktiviert alle Capabilities als Default. Embed-Mode default-sandbox.

---

### ADR-008 — Nur Typed Exceptions, kein Result-Typ in v1

**Datum**: 2026-06-05. **Status**: Akzeptiert.

**Entscheidung**: Error-Handling läuft ausschließlich über `try/catch` mit `throws`-deklarierten Funktionen. Kein `Result<T, E>` als parallele Mechanik.

**Begründung**: Oils Fehler war, beide gleichzeitig zu haben. Eine Wahl, eine Wahrheit. Typed Exceptions sind C#-vertraut, mit `throws` in der Signatur (Swift-Stil) ist Control-Flow explizit.

**Konsequenz**: Wer Result-Stil mag, muss `?T` oder Custom-Enum-Wrapper nutzen — keine Erste-Klasse-Unterstützung.

---

### ADR-009 — Nur `defer`, kein `finally`

**Datum**: 2026-06-05. **Status**: Akzeptiert.

**Entscheidung**: Cleanup-Mechanismus ist ausschließlich `defer` (Go-Stil). `try { ... } finally { ... }` gibt es nicht.

**Begründung**: Oil hatte beides — Doppelung. `defer` ist allgemeiner (überall einsetzbar, nicht nur an `try`) und kompakter.

---

### ADR-010 — Single-Threaded VM, Coroutinen statt Threads in v1

**Datum**: 2026-06-05. **Status**: Akzeptiert.

**Entscheidung**: VM ist single-threaded. Concurrency innerhalb der Sprache läuft über Coroutinen. Parallelismus erfolgt im Host (mehrere VM-Instanzen).

**Begründung**: Multi-Threaded VM ist 5–10x mehr Arbeit (Thread-Safety überall, GC-Koordination, Atomics-Sprachfeatures). Für Game-Scripting und CLI nicht nötig. Coroutinen decken kooperative Concurrency ab.

**Konsequenz**: Lyric-Code teilt nichts cross-thread, weil es keine Threads gibt. Async/Await-Syntax kann post-v1 als Zucker über Coroutinen kommen.

---

### ADR-011 — Implizite Closure-Captures

**Datum**: 2026-06-05. **Status**: Akzeptiert.

**Entscheidung**: Closures capturen Variablen aus dem umgebenden Scope automatisch. Keine explizite Capture-Liste.

**Begründung**: Game-Scripting (`button.onClick(() => player.takeDamage(10))`) lebt von Knappheit. C#-User erwarten implizit.

**Konsequenz**: Risiko von versehentlichen Captures großer Strukturen → GC-Druck. Optional in post-v1: `@noCapture`-Attribut oder Lint-Warning.

---

### ADR-012 — Eine Datei = ein Modul mit Pfad-Inferenz

**Datum**: 2026-06-05. **Status**: Akzeptiert.

**Entscheidung**: Modulname wird aus Dateipfad relativ zum Source-Root abgeleitet. Optional darf Datei einen expliziten `module foo;`-Header haben, der zum Pfad konsistent sein muss.

**Begründung**: Spart Boilerplate. C#/Java haben das mit Package-Headern, aber für Solo-Projekt ist Pfad-Inferenz einfacher.

---

### ADR-013 — `.lyrbc` ist ein plattformneutraler, spezifizierter Vertrag

**Datum**: 2026-07-23. **Status**: Akzeptiert.

**Entscheidung**: Das Bytecode-Format `.lyrbc` wird als eigenständiges, normatives Dokument spezifiziert (`docs/Bytecode.md`, entsteht in M5). Der C#-Serializer ist eine Implementierung der Spec, nicht ihre Definition. Das Format ist plattformneutral: Little-Endian für Fixbreiten-Felder, LEB128 für variable Ints, Strings als längenpräfigierte UTF-8-Bytes, Floats als IEEE-754-Bitmuster. Konstantenpool, Type- und Function-Table enthalten ausschließlich Lyric-Begriffe — keine CLR-Typnamen, keine .NET-Serialisierungsmechanik. Host-/Native-Funktionen werden über eine Import-Tabelle mit symbolischem Namen und Signatur referenziert; Calls verweisen per Index in diese Tabelle (WASM-Modell, Validierung beim Load statt beim Call). Source-Mapping (PC → Datei/Zeile) liegt in einer optionalen, strippbaren Sektion. Encoding ist deterministisch: gleicher Compiler-Input erzeugt byte-identischen Output.

**Begründung**: Ziel-Test: jemand kann allein aus Spec + Import-Tabellen-Katalog einen Disassembler oder eine zweite Runtime schreiben, ohne den C#-Code zu lesen. Das hält eine native Runtime oder ein WASM-Target als spätere, kontainierte Projekte offen, ohne v1 zu verteuern. Determinismus macht Golden-Tests und Bytecode-Diffs trivial. Das Gegenmodell (Lua/CPython: Bytecode als Implementierungsdetail, plattform- und versionsabhängig) passt nicht zu Lyric, weil `.lyrbc` ein First-Class-Auslieferungs-Artefakt ist (`lyric build`, Host-Execution, Hot-Reload).

**Konsequenz**: Format-Version im Header ist von der Compiler-Version entkoppelt; Runtimes lehnen unbekannte Major-Versionen ab. Bis v1.0 darf sich das Format inkompatibel ändern (Version-Bump ohne Migrationspfad) — Stabilitätsversprechen erst ab v1.0. Opcode-Liste und Sektions-Layout bleiben M5-Design; dieses ADR bindet nur die Rahmenbedingungen. M5-Aufwand steigt um das Schreiben von `docs/Bytecode.md` parallel zur Implementierung (geschätzt +3–5 Tage; die Spec ist zugleich Design-Werkzeug).

---

### ADR-014 — `static` als Member-Marker; Konstruktion bleibt zweigleisig

**Datum**: 2026-08-02. **Status**: Akzeptiert.

**Entscheidung**: `ClassMember`/`StructMember` erlauben ein vorangestelltes `static`, sowohl vor einer `FunctionDecl` als auch vor einem `BindingStmt` (`static let`). Ein `static`-Member hat **keinen Empfänger**, kennt kein `this` und ist ausschließlich über den Typ erreichbar (`Account.new(…)`, `Vector3.ZERO`). Ein Member ohne Marker ist Instanz-Member, hat `this` gebunden und ist ausschließlich über eine Instanz erreichbar. Die Kreuzformen sind Fehler. `static mut fn` ist ein Fehler (kein Empfänger, der mutieren könnte). Konstruktion bleibt zweigleisig: der Struct-Init-Ausdruck `T { … }` für reine Daten, eine `static fn` für Konstruktion mit Logik. `new` bleibt **kein** Keyword — nur Konvention. Companion-Objekte werden **nicht** eingeführt.

**Begründung**: Ohne Marker war jede Methode zugleich statisch und instanzgebunden — gemessen am 2026-08-02 gingen `P.getHp()` (Instanzmethode ohne Empfänger), `p.new()` (Fabrik auf einer Instanz) und sogar `this.hp` in einer als `P.new()` gerufenen Methode allesamt durch die Typprüfung. Das ist keine Unterspezifikation, sondern eine Lücke, die erst beim Lowering (M7/P1) sichtbar wurde und dort einen Feldzugriff ohne Objekt erzeugt hätte. — Der explizite Marker ist die Antwort, die zu einer bereits getroffenen Entscheidung passt: `this` ist in Lyric ein Keyword-Ausdruck (§6), kein deklarierter Parameter. Damit steht die Sprache in der C#/Java-Familie, und dort heißt „Member ohne Empfänger" `static`. Der Rust/Python-Weg (expliziter `this`-Parameter, der zugleich `mut` überflüssig machen würde) wäre das sauberere Gesamtdesign, kehrt aber die `this`-Entscheidung um und ändert jede Methodensignatur in Spec, Doku und Beispielen. — `static let` erledigt typgebundene Konstanten (`Vector3.ZERO`) und nimmt damit Companions ihr stärkstes Argument; was ohne Objektidentität von einem Companion bliebe, ist `static` mit Klammern, und mit Identität zöge er Initialisierungsreihenfolge, `this` im Companion und Companion-Vererbung nach. Companions wären zudem ein zweiter Namensraum neben Modulen — bei ADR-012 (eine Datei = ein Modul) meist derselbe. — Ein reserviertes `new` als einzige empfängerlose Form wurde verworfen, weil Lyric kein Overloading hat (ADR-015): es gäbe genau **einen** Konstruktor pro Typ, und jede weitere Fabrik (`Account.fromJson`) wäre heimatlos. In C#/Java fällt das nicht auf, weil Overloading die zweite und dritte Variante auffängt — genau die Stütze, die hier fehlt.

**Konsequenz**: Ein Keyword mehr in §2. Grammatik, Sema-Regeln und die Migration der `fn new`-Fabriken in `examples/bank.lyr`, dessen Sema-Kopie, `Sprache.md` §3.3 und `Doku.md`. Für M7/P3 ist der Marker die Antwort auf „was steht in der vtable": Instanz-Member ja, `static` nein. Methoden-Lowering (in P1 bewusst ausgelassen) wird damit entblockt.

> **Korrektur (2026-08-02, bei der Umsetzung in P1b):** Die erste Fassung dieses ADRs verbot `mut` zusätzlich an **Klassen**-Methoden mit der Begründung, es setze dort nichts durch und sehe nur aus wie eine Zusicherung. Das war falsch, in zwei Richtungen. Erstens dokumentiert `Doku.md` §10.2 den Marker ausdrücklich als *Lesbarkeits-Konvention* für Klassen — er hat nie behauptet, etwas durchzusetzen. Zweitens deklarieren Interfaces `mut fn` (etwa `Damageable.takeDamage`), und eine implementierende Klasse muss die Signatur erfüllen können; das Verbot hätte die Konformanz gebrochen. Es bleibt bei einer Regel für die Marker-Kombination: **`static mut fn`** ist ein Fehler, weil es keinen Empfänger gibt, über den `mut` sprechen könnte.

---

### ADR-015 — Funktions-Overloading auf v1.X vertagt

**Datum**: 2026-08-02. **Status**: Akzeptiert.

**Entscheidung**: Zwei gleichnamige Funktionen oder Methoden in demselben Scope bleiben in v1 ein Fehler (`LYR-RES0001`). Overloading wandert in die v1.X-Skizze, neben das bereits dort stehende Operator-Overloading. Dies ist eine **Vertagung, kein Ausschluss**.

**Begründung**: Zuerst die Richtigstellung — es gab hierzu nie eine Entscheidung. Die Behauptung „Lyric hat kein Overloading" stand in `Doku.md` und in der M6-Korrektur dieses Dokuments; beide Stellen entstanden 2026-08-02 als Begründung dafür, dass `println` nur `string` nimmt, und beide sind falsch: jede andere Erwähnung von „Overloading" in der Spec meint das **Operator**-Overloading. Dass der Compiler es ablehnt, ist eine Folge davon, dass `SymbolTable` eine Name→Symbol-Map ist — eine Implementierungs-Eigenheit, keine Position. — Zur Sache: Overloading ist in Lyric **billiger als sein Ruf**, weil der Löwenanteil von C#s „better function member" die impliziten Konvertierungen sind, die es hier nicht gibt (§6.5 verlangt `as` schon für `int8` → `int32`). Teuer bleibt es an vier Stellen, und alle vier sind die, die dieser Sprache ohnehin schwerfallen: untypisierte Literale, die sich dem Kontext anpassen (`f(int8)` vs. `f(int64)` bei `f(5)`); Default-Argumente (`f(int)` vs. `f(int, int = 0)` bei `f(1)`); Lambdas mit bidirektionaler Inferenz, wo Argumenttyp und Überladungswahl zirkulär voneinander abhängen; und `extend`-Methoden, deren Überladungsmengen Modulgrenzen mit Sichtbarkeitsregeln kreuzen. Dazu müsste `NameMangling` die Signatur in den Symbolnamen ziehen, und Symbolnamen sind Bytecode-Vertrag (ADR-013). — Die beiden Fälle, die den Wunsch ausgelöst hatten, haben bessere Antworten: `println` will `println<T :: [Display]>(v: T)` (M8, Builtin-Konformanz), nicht drei Überladungen; mehrere Fabriken wollen sprechende Namen (`fromJson`) oder Default-Argumente. Go und Zig verzichten vollständig und gelten nicht als kaputt. — Ausschlaggebend ist die Richtung: **Overloading ist additiv.** Es lässt sich in v1.2 einführen, ohne ein einziges v1.0-Programm zu brechen, weil zwei gleichnamige Funktionen vorher nie erlaubt waren. Umgekehrt geht es nicht. Bei dieser Asymmetrie ist Vertagen billig und reversibel, Einführen ist beides nicht — und der richtige Zeitpunkt ist der, an dem die vier Kostenstellen stabil stehen.

**Konsequenz**: Die falschen Sätze in `Doku.md` und der M6-Korrektur werden auf die tatsächliche Begründung umgeschrieben (`println` nimmt `string`, weil `Display`-Konformanz für Builtins erst in M8 entsteht). `LYR-RES0001` bekommt bei gleichnamigen Funktionen eine Meldung, die die Regel benennt, statt wie eine bloße Kollision zu klingen. Eintrag in der v1.X-Tabelle.

---

### ADR-016 — `T[]` ist ein echtes Array; Collections dispatchen über `Indexable<T>`

**Datum**: 2026-08-02. **Status**: Akzeptiert.

**Entscheidung**: `T[]` ist ein **echtes Array**: eine Referenz auf einen zusammenhängenden Speicher, dessen Länge bei der Erzeugung feststeht und sich danach nicht ändert. Es ist ein eingebauter Typ, kein Zucker für eine Bibliotheksklasse. Gebaut wird es aus dem Literal `[a, b, c]`, der Wiederholung `[x] * n` und der Konkatenation `xs + ys` (alle drei bereits in `Sprache.md` §6.5 als eingebaute Semantik festgelegt). `[i]` auf einem `T[]` lowert **direkt** zu `ldelem`/`stelem`, ohne Dispatch. Für jeden anderen Typ ist `[i]` Zucker für ein wohlbekanntes Interface `Indexable<T>` mit `fn get(i: int): T` und `mut fn set(i: int, value: T)`. Wachsende Container (`List<T>`, `HashMap<K, V>`) sind gewöhnliche generische Klassen in `std.collections`, die intern ein `T[]` halten, bei Bedarf umkopieren und `Indexable<T>` sowie `Iterator<T>` implementieren. **`T[N]` entfällt in v1.** Ein Element-Index außerhalb der Grenzen ist ein `panic` (§9).

**Begründung**: Das Gegenmodell — `T[]` als Zucker für `List<T>`, wie es `Doku.md` §5.2 und `Sprache.md` §4 bis hierher beschrieben — ist das Python-Modell und war nie beabsichtigt. Es hat zwei harte Probleme. Erstens **Bootstrapping**: dispatcht `[i]` immer über ein Interface, dann braucht dessen Implementierung selbst einen indizierten Speicher — man landet zwangsläufig bei einem Primitiv darunter, also kann man es auch benennen. Zweitens **Kosten ohne Gegenwert**: jedes Array trüge Kapazitätsverwaltung mit sich, auch dort, wo nie etwas wächst. Rust (`[T; N]`/Slices primitiv, `Index`-Trait für den Rest) und C# (Arrays primitiv, Indexer für den Rest) schichten aus demselben Grund so. — Die Bindung von `[i]` an ein Interface ist **kein neuer Mechanismus**: `Sprache.md` §6.5 verlangt für `for-in` bereits einen Ausdruck, der `Iterator<T>` implementiert. Eine eingebaute Syntaxform, die an einen benannten Kontrakt bindet, ist damit schon Teil der Sprache; `Indexable<T>` wendet dieselbe Regel ein zweites Mal an. Das ist ausdrücklich **nicht** das in ADR-015 vertagte user-defined Operator-Overloading — dort geht es um beliebige Operatoren, hier um eine feste, kleine Menge von Sprach-Kontrakten. — **`T[N]` entfällt**, weil sein einziger verbleibender Zweck die Länge im Typ wäre: `int[3]` und `int[5]` würden verschiedene Typen mit eigenen Zuweisungsregeln, also viel Typsystem-Oberfläche für wenig Gewinn. Die Ergonomie-Lücke, die es hätte füllen müssen — „ein Array der Länge n mit Default-Werten" —, füllt `[0] * n` bereits, und zwar mit `n` als Laufzeitwert.

**Konsequenz**: `Doku.md` §5.2 und `Sprache.md` §4 werden korrigiert; beide behaupteten bisher das Gegenteil. Aus dem Bytecode-Format fallen die in der ersten P2-Fassung specten Opcodes `push`/`pop` wieder heraus — sie gehören `List<T>`, nicht `T[]` — und dafür kommen `arrcat`/`arrrep` hinein, weil Konkatenation und Wiederholung spezifizierte Sprachsemantik sind. P2 liefert damit `newarr`, `ldelem`, `stelem`, `arrlen`, `arrcat`, `arrrep`. `Indexable<T>` selbst ist erst mit Interfaces (P3) und Generics (P8) baubar; bis dahin ist `[i]` ausschließlich auf `T[]` gültig. `examples/stack.lyr` muss auf `List<T>` umgeschrieben werden und wandert zu M8 — es benutzt heute `items: T[] = []` mit `.push`, also genau das verworfene Modell.

---

## Was nach v1.0 kommt

Wir schreiben **keine** ausführliche post-v1-Roadmap (das war einer der Oil-Fehler — das Dokument verselbständigte sich). Hier nur eine sehr kurze Skizze, was *vielleicht* kommt, in vagem Prioritäts-Sortier. Konkretisierung erfolgt erst, wenn die jeweilige Phase angefangen wird.

| Phase | Inhalt | Wann |
|---|---|---|
| **v1.1** | DateTime, Regex, JSON in Stdlib. Newtypes. `pub(package)`. Raw-Strings. | wenn echter Bedarf erkennbar |
| **v1.2** | LSP-Server (Diagnostics-Streaming, Hover, Go-to-Definition) | wenn Sprache produktiv ist |
| **v1.3** | Async/Await-Syntax als Zucker über Coroutinen | wenn Async-Code stark gefragt |
| **v1.4** | User-defined Operator-Overloading; Funktions-Overloading (ADR-015) | wenn Math-Libs schreien |
| **v1.5** | Formatter `lyric fmt` | wenn Community entsteht |
| **v1.X?** | JIT-Backend (Cranelift), Package-Manager | nur bei demonstriertem Bedarf |

**Niemals** geplant:
- Class-Inheritance.
- Result-Typ als zweiter Error-Mechanismus.
- Raw-Pointer / `unsafe`.
- Eigener Debugger (.NET-Debugger reicht).
- Eigene Package-Registry (Solo-Projekt).
- Multi-Editor-Plugins jenseits VS Code (community-aufgabe).
