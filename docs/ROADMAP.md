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
│  ├─ Lyric.Core/          → lyrcore.dll  # Diagnostics, SourceManager, Span
│  │  └─ Bytecode/                        #   Bytecode-Format: Leseseite (ADR-017)
│  ├─ Lyric.Frontend/      → lyrfe.dll    # Alles zwischen Quelltext und Bytes:
│  │  ├─ Lexing/                          #   Tokenizer
│  │  ├─ AST/                             #   AST-Typen, Dumper
│  │  ├─ Parsing/                         #   Recursive-descent + Pratt
│  │  ├─ Resolver/                        #   Module-Auflösung, Imports
│  │  ├─ Sema/                            #   Type-Checker, Generics-Monomorph
│  │  ├─ Ir/                              #   Typed Mid-IR
│  │  ├─ Emit/                            #   Bytecode-Format: Schreibseite (ADR-017)
│  │  └─ Compiler/                        #   Pipeline Quelle → IR → Bytes (ADR-017)
│  ├─ Lyric.Vm/           → lyrrt.dll     # Interpreter, Value-Repr, GC-Hook
│  ├─ Lyric.Embedding/   → lyrembed.dll  # Host-API: LangVm (M10)
│  ├─ Lyrc/               → lyrc.exe      # Compiler (ADR-017)
│  ├─ Lyrvm/              → lyrvm.exe     # Runtime  (ADR-017)
│  └─ Lyric.Cli/          → lyric.exe     # Treiber  (ADR-017)
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
- **`Lyric.Embedding.LangVm`** (`lyrembed.dll`) — Embedding-Hauptklasse. Compile, Run, RunScript, Call, RegisterFunction, RegisterType, Reload. *(Stand bis 2026-08-11 als `Lyric.Vm.LangVm` hier — falsch: die Runtime darf das Frontend nicht referenzieren, siehe den Zuschnitt bei M10.)*
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
| P3b ✅ | Enums (Unit-/Tuple-/Struct-Varianten) + `match` | `examples/enums.lyr` |
| P3 ✅ | Interfaces + vtable-Dispatch (**nach** P3b) | `examples/interfaces.lyr` |
| P4 | Structs (Wert-Semantik, Copy-on-Assign) | `examples/vectors.lyr` |
| P5 ✅ | Exceptions + `defer` (LIFO auf jedem Exit-Pfad) | `examples/bank.lyr` |
| P6 | Closures (Lifting + Environment-Objekt) | `examples/closures.lyr` |
| P7 | Coroutinen (State-Machine-Lowering, ADR-006) | `examples/generator.lyr` |
| P8 | Generics-Monomorphisierung + `for-in`/`Iterator` | `examples/fizzbuzz.lyr`, `examples/stats.lyr` |

- IR-Instruktionen: `NewClass`, `NewStruct`, `NewArray`, `LoadField`, `StoreField`, `LoadElem`,
  `StoreElem`, `ArrayLen`, `CallVirt`, `Throw`, `Catch`, `Yield`, `Resume` — aus M5s Liste
  nachgeholt (siehe Korrektur unten).
- Bytecode-Format **1.2**: Types-Sektion (Id 3) wird geschrieben, zusammengesetzte Typ-Tags ab
  `0x40`. Beides ist in `docs/Bytecode.md` bereits reserviert; kein Formatbruch nötig.
  Stand nach P3: **2.1** (Enums erzwangen 2.0, Interfaces ergänzen additiv Kind 2, Sektion 8 und
  zwei Opcodes).
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
>
> *(Korrektur 2026-08-07, beim Bau von P9 gemessen: „`extend` ist in der Sema fertig" stimmte nur
> für die **inhärente** Form. `extend T :: [I]` scheiterte an `LYR-SEM0001` — `IsAssignable`
> kannte Extension-Konformanz nicht, obwohl `Satisfies` sie für Constraints längst akzeptierte.
> Zwei Antworten auf „erfüllt T das Interface I", 1200 Zeilen auseinander. Orphan-Rule und der
> Konformanz-Check **innerhalb** des Blocks waren fertig, das nominale Subtyping nicht.)*

> **Korrektur (2026-08-05, vor P4):** P4s Gate war `examples/bank.lyr`. Das Programm enthält
> **kein einziges `struct`** — es zeigt `Throwable`, `throws`, `try`/`catch` mit typed catch und
> `defer`. Das ist die Liste von **P5**, Zeile für Zeile. Seine drei heutigen Blocker sind
> Feld-Defaults (ein eigener offener Posten), Exceptions und `defer`; Wert-Semantik kommt darin
> nirgends vor.
>
> Es wandert zu P5 und ist dort ein gutes Gate. P4 bekommt `examples/vectors.lyr`: Zuweisung
> kopiert, Parameter kopiert, `struct` im `struct` kopiert mit, Methoden-Mutation bleibt lokal.
>
> **Das ist das vierte Mal**: M5 an `hello.lyr`, M6 an FizzBuzz/Fibonacci, P2 an `stats.lyr`,
> P3 an `shapes.lyr` (das kein Interface enthielt). Das Muster ist immer dasselbe — ein Gate wird
> nach *Komplexität* ausgewählt statt nach den Sprachmitteln, die der Slice liefert. **Die Regel,
> die daraus folgt: ein Gate muss das Schlüsselwort enthalten, um das der Slice geht.** Bei P4
> hätte ein `grep struct examples/bank.lyr` gereicht.
>
> *(Zur Ratifizierung im nächsten Scope-Check.)*

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


> **Lieferposten-Inventur (2026-08-06, nach P5).** Jedes Konstrukt aus `Sprache.md` wurde durch
> Parser, Sema und Lowering gefahren — 38 Fälle, davon laufen **12** bis zur IR durch. Das Ergebnis
> ist nicht die Zahl, sondern **was keinem Slice gehört**.
>
> **A — hat einen Slice, alles in Ordnung** (kein Handlungsbedarf):
> Closures, Lambda-Block, Funktionstyp → **P6**. Coroutinen/`yield`/`resume` → **P7**.
> Generische Funktionen und Klassen, `for-in` über Range und Array → **P8**.
> `f"{x:N2}"`-Format-Specs → **M8** (`std.fmt`, so schon vermerkt).
>
> **B — gehört keinem Slice.** Das ist die eigentliche Ausbeute:
>
> | Konstrukt | `Sprache.md` | Stufe, an der es scheitert |
> |---|---|---|
> | ~~`static let` (typgebundene Konstante)~~ → **P5c** | §3.2, ADR-014 | Lowering — hing an den Konstanten |
> | ~~`@test` und jedes andere Attribut~~ → **post-v1** (2026-08-06) | §10 | Grammatik fehlt; Entscheidung getroffen |
> | ~~`fn main(args: string[])`~~ → **P5b-4** (meldet sich jetzt) | §11 | **Lowering, stumm** — erzeugte ein Bibliotheks-Modul ohne Start-Sektion |
> | ~~`match` über Nicht-Enums (Literale, `\|`, Ranges, Guards)~~ → **P5b-1** | §5, §6.3 | Lowering — „'int' is not an enum" |
> | ~~`?.` (Optional-Chaining)~~ → **P5b-2** | §7 | Lowering |
> | ~~`??=` (Coalescing-Assign)~~ → **P5b-2** | §7 | Lowering |
> | ~~`string + string`, `string * int`~~ → **P5b-3** | §6.5 | Lowering |
> | ~~`panic(msg)`~~ → **P5b-3** | §9 | Lowering — `std.core.panic` fehlte |
> | ~~Default-Argumente~~ → **P5b-5** | §3.1 | Lowering |
> | ~~`params`-Variadics~~ → **P5b-5** | §3.1 | Lowering |
> | `extend`-Blöcke → Vorschlag **P9** | §3.6 | Lowering |
> | Tupel als Typ und Wert | §4 | Lowering — **hat bis heute keinen Slice** |
> | ~~Modul-`let` / Konstanten~~ → **P5c** (Globals-Sektion, Format 2.4) | §2.3 | Lowering (war bekannt) |
>
> **C — drei Stellen, an denen STATUS/ROADMAP etwas Falsches behaupten.** Das ist der ernste Teil,
> weil genau diese Sätze die Inventur bisher verhindert haben:
>
> 1. *(Zurückgezogen 2026-08-06, noch am selben Tag.)* Hier stand, `static let` parse nicht. **Das
>    war falsch, und der Fehler lag in der Probe**: sie ließ das `;` weg, das `BindingStmt` laut
>    §3.2 verlangt. `static let Z: int = 0;` parst und typprüft; es scheitert erst im Lowering, an
>    derselben Stelle wie jede andere Konstante. STATUS' ursprünglicher P1b-Satz war richtig.
>
>    Die Lehre ist unbequem und gehört trotzdem hierher: **eine Inventur ist nur so gut wie ihre
>    Proben.** Ein `LYR-PAR####` aus einem Test heißt zuerst „mein Testprogramm ist falsch" und
>    erst danach „der Compiler kann es nicht". Bei den Lowering-Befunden ist das Risiko kleiner —
>    dort steht die Grenze als Klartext in der Meldung.
> 2. STATUS zu **P2b**: „`??`, `??=` und `?.` … lowern zu Verzweigungen über `optissome`." Nur `??`
>    tut das. `??=` und `?.` sind `LYR-IR0001`.
> 3. STATUS zu **P3b**: „`match` als Ausdruck und Statement". Nur über **Enums**. `match (5)`,
>    `match ("a")` und `match (true)` scheitern alle — dabei ist die Exhaustivitätsprüfung dafür
>    seit M4 da.
>
> **Warum es wieder passiert ist.** Dieselbe Mechanik wie bei den Gates (viermal) und bei M5/M6:
> ein Slice wird an seinem Gate-Programm gemessen, und das Gate benutzt nur einen Teil dessen, was
> der Slice laut Titel liefert. `optionals.lyr` benutzt kein `?.`, `enums.lyr` kein `match (5)`.
> Der Slice gilt als fertig, der Rest verschwindet — und `LYR-IR0001` sieht dabei aus wie eine
> geplante Grenze.
>
> **Konsequenz, konkret.** Die Restliste von M7 ist nicht P6/P7/P8, sondern:
>
> | Slice | Inhalt |
> |---|---|
> | ~~**P5b**~~ ✓ | Die Lücken aus B, soweit sie Kernsprache sind: `match` über Nicht-Enums, `?.`, `??=`, `string +`/`*`, `panic`, `main(args)`, Default-Argumente, `params`. **Tupel sind dabei herausgefallen** — sie sind ein *Typ* und keine Aufrufform, und brauchen deshalb einen eigenen Slice |
> | ~~**P5c**~~ ✓ | Konstanten: Modul-`let` und `static let` als Globals-Sektion (Format 2.4) |
> | P6 | Closures |
> | P7 | Coroutinen — Gate am 2026-08-06 von `fibonacci.lyr` auf `generator.lyr` gewechselt: das alte benutzt `for-in`, also P8 |
> | P8 | Generics + `for-in`/`Iterator` |
> | ~~**P9**~~ ✓ | `extend` — hatte bis 2026-08-06 in keiner Slice-Tabelle gestanden; P9a inhärent, P9b über Interface, P9c Gate |
>
> Und für M9: **`@test` hat keine Grammatik.** §2.3 sieht an einer Deklaration kein Attribut vor,
> §10.1 versprach es. *(Entschieden am 2026-08-06: Attribute und `lyric test` gehen zusammen nach
> post-v1 — siehe die Korrektur bei M9.)*
>
> **Regel daraus, in derselben Reihe wie die Gate-Regel:** ein Slice ist fertig, wenn seine
> Lieferposten Punkt für Punkt abgehakt sind — nicht, wenn sein Gate läuft. Diese Inventur ist am
> Ende jedes Meilensteins zu wiederholen; das Skript dafür ist trivial (jedes Konstrukt aus
> `Sprache.md` durch `lyrc parse`/`check`/`lower`).
>
> *(Zur Ratifizierung im nächsten Scope-Check.)*


### M8 — Stdlib (4–6 Wochen)

**Ziel**: Alle 14 Stdlib-Module produktiv.

**Lieferposten**:
- `std.core`, `std.option`, `std.error`, `std.string`, `std.fmt`, `std.math`, `std.collections`, `std.iter`, `std.coroutine`, `std.io.console`, `std.io.file`, `std.os`, `std.dotnet`.
- Native-Hooks für Performance-kritische Operationen (z.B. `List<T>` direkt auf `System.Collections.Generic.List<>`).
- Capability-Enforcement: Imports von permission-gated Modulen prüfen Capabilities zur Resolve-Zeit.
- Diagnostik-Codes `LYR-CAP0001..0010`.
- Pro Modul: 10+ Unit-Tests.

**Exit**: Stdlib-Tests grün. Beispiel-CLI-Tool nutzbar (z.B. ein simples `wc`-Klon).

> **Korrektur (2026-08-07, beim M8-Plan): `std.io.net` ist aus M8 gestrichen** und steht in der
> v1.X-Tabelle. Es war als „minimal HTTP/TCP" ein Lieferposten neben dreizehn anderen Modulen —
> tatsächlich ist es ein eigener Meilenstein: Sockets, Adressauflösung, Timeouts, Fehlerklassen,
> und vor allem eine **Designfrage, die M8 sonst mitentscheiden müsste**. ADR-010 hat kein
> Multithreading, und die Coroutinen aus P7 sind kooperativ ohne Scheduler; was ein blockierender
> Socket in einer Single-Thread-VM bedeutet, ist keine Fleißarbeit, sondern eine Entscheidung über
> das Nebenläufigkeitsmodell. Sie gehört nicht als Nebenprodukt in einen Stdlib-Meilenstein.
>
> **Der Meilenstein verliert damit keinen Exit-Posten**: der `wc`-Klon braucht Dateien und
> Strings, kein Netzwerk.
>
> `std.dotnet` bleibt vorerst drin, ist aber der nächste Kandidat: es ist Interop und teilt sich
> die Marshalling-Schicht mit **M10**. Zweimal entworfen wäre einmal zu viel.
>
> **Nachtrag (2026-08-11, beim M10-Plan): `std.dotnet` ist gestrichen** und steht in der
> v1.X-Tabelle. Der Grund ist nicht Aufwand, sondern Richtung: es ist eine Reflection-Brücke —
> beliebige .NET-Typen zur Laufzeit, ohne Deklaration — und damit die **Umkehrung** dessen, was
> M10 baut. Dort entscheidet der Host, was ein Skript sehen darf (ADR-007); mit `std.dotnet`
> entschiede das Skript. Beides zugleich anzubieten hiesse, die Sandbox mit der einen Hand zu
> bauen und mit der anderen eine Tür danebenzusetzen.
>
> Das Capability-Bit `hostAccess` (Wert `0x8`) **bleibt reserviert** — dieselbe Regel wie bei
> `networkAccess`: eine Nummer, die später etwas anderes bedeutet, macht jedes ältere `.lyrbc`
> falsch (`Bytecode.md` §Capabilities).

> **Korrektur (2026-08-10, M8b/S9): `std.error` und `std.coroutine` sind gestrichen.** Beide waren
> als Modul geführt, und beider dokumentierter Inhalt **ist bereits die Sprache**: `Throwable` ist
> ein eingebautes Interface mit synthetischem AST, `Coroutine<T>` ein eingebauter Typ, `yield` und
> `resume` sind Schlüsselwörter. Ein Modul daneben wäre der Doppel-Mechanismus aus Rule 2.
>
> Von `std.error` blieb **eine** Klasse übrig (`Exception`); sie steht in `std.core`. Ein Modul für
> eine Klasse trägt seinen Namen nicht. `NullDereferenceError` und `CoroutineEndedError` entstehen
> nicht: beide Fälle bleiben `panic`, weil §17.1 `throw` den Domain-Fehlern und `panic` den
> Programmierfehlern zuweist — und weil geprüftes `throws` (§9) transitiv propagiert. Gemessen am
> 2026-08-10: die Stdlib enthält **null** `throws`-Deklarationen, ein Force-Unwrap und 124 Stellen
> mit Division oder Index-Zugriff. Die Fehler fangbar zu machen hieße, `throws` durch die gesamte
> Bibliothek und durch die Interfaces `Ordered`, `Hashable` und `Display` zu treiben, um ein
> Konstrukt zu bedienen, das die Stdlib einmal benutzt. *(Dass §7 und §8 heute etwas anderes
> behaupten, ist dort korrigiert.)*
>
> `std.coroutine` bräuchte als einzigen Inhalt die Brücke `Coroutine<T>` → `Iterator<T>`. Sie ist
> nicht schreibbar, solange das Ende ein Panic ist: der `resume`-Aufruf, der den Rumpf durchlaufen
> lässt, ist derselbe, der daran stirbt — ein Prädikat käme immer ein `resume` zu spät. Der Umbau
> (der erzeugte Sprungverteiler liefert intern `?T`, `resume` packt aus und behält seinen Vertrag)
> ist Lowering-Arbeit in der Größenordnung eines P-Slices und **keine Stdlib-Aufgabe**. Er ist
> nicht abgelehnt, nur nicht hier.
>
> M8 verliert damit zwei Lieferposten und behält elf Module.

### M9 — REPL + Tests + Tooling (2–3 Wochen)

**Ziel**: User-Experience rund.

**Lieferposten**:
- `lyric repl`: interaktive REPL mit Persistent-Environment.
- TextMate-Grammar für VS Code (Syntax-Highlighting): `tooling/vscode-lyric/syntaxes/lyric.tmLanguage.json`.
- Minimale VS-Code-Extension (Highlighting + Run-Command).
- README.md, LICENSE, CONTRIBUTING.md.
- Examples-Verzeichnis aufgefüllt.

**Exit**: `lyric repl` produktiv. **v0.9 Release-Tag**.

> **Erreicht (2026-08-07).** `lyric repl` laeuft; die REPL ist ein eigenes Binary geworden
> (**ADR-021**), und der Dispatch dorthin ist eine Zeile — genau der Test, den ADR-019 sich selbst
> gestellt hatte. Dazu: README auf den Stand gebracht und **maschinell geprueft** (ihr Beispiel
> wird ausgefuehrt), TextMate-Grammar gegen den Lexer gebunden, VS-Code-Extension mit Run-Command.
>
> **`lyric test` ist gestrichen** (Korrektur unten), die Beispiele waren mit 22 Programmen bereits
> gefuellt. Was M9 nicht bringt, steht ausdruecklich in der v1.X-Tabelle: LSP, Formatter,
> Attribute.

> **Abgeschlossen erst 2026-08-10 (S6), und das ist der Punkt.** Der Eintrag darueber stand seit
> dem 2026-08-07 da; **vier Lieferposten liefen zu diesem Zeitpunkt nicht**, und das Exit-Kriterium
> („`lyric repl` produktiv") hat das nicht gemerkt, weil es die REPL misst und nicht den
> Meilenstein:
>
> - **`dotnet test` war in Release rot**, seit 60 Pushes und damit seit *vor* M9/S1. Ein einziger
>   Test trug die `--verbose`-Phasenliste als Literal, inklusive `verify` — das laeuft nur in
>   Debug-Builds. Der Maintainer testete Debug, CI testete Release, und der Badge in der README
>   zeigte die ganze Zeit auf den Fehlschlag.
> - **`build/publish.proj` war von `.gitignore` erfasst** (`build/`) und lag in keinem Clone. Der
>   CI-Job „Publish toolchain" ruft es auf und ist nie gelaufen, weil `needs:` ihn uebersprang. Die
>   Auslieferung — Rule 3s Artefakt — war damit nie gebaut worden, nur behauptet.
> - **README und `Doku.md` §23.7 behaupteten, M9 sei nicht gebaut**: „What is missing … is the
>   REPL, editor integration", ein Projektbaum mit drei Binaries ueber einem Abschnitt „The four
>   binaries", und ein Auslieferungs-Verzeichnis ohne `lyrrepl.exe`.
>
> **Das ist das sechste Mal dasselbe Muster** — nach M5 (`hello.lyr`), M6 (FizzBuzz/Fibonacci), P2
> (`stats.lyr`), P3 (`shapes.lyr`), P4 (`bank.lyr`) und der Lieferposten-Inventur nach P5. Die
> Regel dagegen war 2026-08-02 formuliert und stand in beiden Dateien; angewandt wurde sie bei M9
> nicht. **Die Konsequenz ist keine neue Regel, sondern ein Zeitpunkt**: die Inventur gehoert vor
> den Tag, nicht danach — ein Tag ist das einzige, was sich nicht stillschweigend nachbessern
> laesst.
>
> Getaggt: `m9-complete` und annotiert `v0.9.0`, beide auf den S6-Commit.

> **Korrektur (2026-08-06):** `lyric test` ist aus M9 gestrichen und wandert mit den Attributen
> nach post-v1. Es sammelt `@test`-Funktionen — und `@test` hat keine Grammatik: `Sprache.md` §2.3
> sieht an einer Deklaration kein Attribut vor, §10.1 versprach eines. Die Lücke bestand seit M1
> unbemerkt, weil kein Beispiel und kein Test ein Attribut benutzte; die Lieferposten-Inventur hat
> sie gefunden.
>
> Sie zu schließen hieße, hier eine Sprachentscheidung zu treffen (nur an Deklarationen? auch an
> Parametern? mit Argumenten?) — für ein Werkzeug-Thema, das kein Programm braucht. `Sprache.md`
> §10 ist entsprechend umgeschrieben: die Syntax bleibt reserviert, Parser und Sema lehnen sie mit
> einer Meldung ab, die den Grund nennt.
>
> M9 verliert damit einen Lieferposten und behält REPL, Editor-Integration und die Beispiele.
> *(Zur Ratifizierung im nächsten Scope-Check.)*

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

> **Zuschnitt (2026-08-11, vor E1).** M10 läuft in sechs Slices, jeder mit eigenem Gate:
> **E1** `LangVm` + `Compile`/`Run` + Capabilities · **E2** `Call<T>` und Skalar-Marshalling ·
> **E3** `RegisterFunction` · **E4** `RegisterType<T>` · **E5** `Reload` · **E6** Doku, Beispiel,
> Inventur. `std.dotnet` ist gestrichen (Nachtrag bei M8).
>
> **`Lyric.Embedding` ist eine eigene Assembly** (`lyrembed.dll`) und **nicht** Teil von
> `Lyric.Vm`, wie §Zentrale Komponenten oben es bis heute behauptet hat. Eine Embedding-API
> übersetzt *und* führt aus; läge sie in `Lyric.Vm`, müsste `lyrrt` das Frontend referenzieren —
> und der Architektur-Test, der festhält dass `lyrvm.exe` weder `lyrfe.dll` noch `stdlib/`
> ausliefert, fiele. ADR-021 hat den Fall bereits entschieden: `lyrrepl` war das erste Artefakt
> mit beiden Seiten, `lyrembed` ist das zweite. **Die Zeile in §Zentrale Komponenten ist damit
> falsch und hier korrigiert.**
>
> **E4a ist erledigt** (2026-08-11): Host-Objekte reisen durch ein Skript. `RegisterType<T>` macht
> einen .NET-Typ als **opaken** Lyric-Typ sichtbar; das Skript reicht ihn weiter und kann sonst
> nichts damit — kein Feldzugriff, keine Konstruktion (`LYR-SEM0061`).
>
> **Format 3.0**: `TypeTag.Host = 0x47` traegt seinen Namen inline. Eigenes Tag neben `Ref`, und
> das ist der Kern — bei `Ref` kennt das *Modul* das Layout, bei `Host` der *Host*. Ein Host-Typ
> hat deshalb **keinen Typtabellen-Eintrag**, womit ADR-026s Zusage „nie ein `ldfld`"
> **strukturell** wird statt geprueft. Major-Bump, weil §2 einer neuen Minor nur ueberspringbare
> Sektionen erlaubt und ein Typ-Tag keine ist.
>
> **Was E4a noch nicht hat**: Methoden auf einem Host-Typ (`e.damage(5)`) — das ist E4b samt
> Builder-API und Beispiel-Host. Heute gehen freie Funktionen: `damage(e, 5)`.
>
> **E3 ist erledigt** (2026-08-11): `RegisterFunction` leitet die Lyric-Signatur aus dem
> .NET-Delegaten ab und schreibt sie als bodylose `pub fn` in ein synthetisches Modul **`host`**,
> das der Compiler wie jede Stdlib-Deklaration sieht. Der Native-Seam aus M6 traegt sie — gebunden
> beim Laden ueber den Namen. Kein zweiter Mechanismus.
>
> **Das Skript muss `host` importieren**, und damit ist `Doku.md` §21 endgueltig als nicht baubar
> erwiesen: dort ruft ein Skript `playSound("hit")` ohne Import. §2.2 kennt keinen impliziten
> Namensraum, und einen fuer genau eine Sorte Funktion einzufuehren waere ein Sonderweg. §21 wird
> in E6 gegen das Gebaute neu geschrieben.
>
> Eine Host-Funktion kostet **keine Capability** — der Host hat sie selbst hingestellt. Die Stufen
> aus ADR-007 gelten der Stdlib; was darueber hinausgeht, entscheidet der Host einzeln.
>
> **E2 ist erledigt** (2026-08-11): `ScriptInstance.Call<T>`/`CallVoid` und die
> Skalar-Marshalling-Schicht (alle vierzehn Skalartypen plus `string`, verlustfrei oder gar nicht).
> **Abweichung von der Skizze oben, mit Grund**: `Call` sitzt auf einer `ScriptInstance`, nicht auf
> `LangVm`. Die Lieferposten-Zeile las sich, als habe eine VM genau ein Skript; sobald sie zwei hat
> — ein Host mit zwei Mods ist der Normalfall —, müsste `Call` raten oder es gäbe ein implizites
> „aktuelles Skript". Die Instanz ist zugleich der Ort, an dem die Modul-Konstanten leben, und
> damit fällt ADR-025s Reload-Zusage in E5 von selbst heraus.
>
> Dabei aufgefallen: **der Modulname des Hosts erreichte den Compiler gar nicht.** `ScriptSource`
> setzte nur den Anzeigenamen für Diagnosen, die Modul-Identität blieb `main` — zwei Mods hätten
> beide `main` geheißen, und ein Aufruf über den Namen fände die Funktion des falschen. Gefunden
> vom ersten Test, der eine Funktion beim Namen rief.
>
> **E1 ist erledigt** (2026-08-11): `LangVm` mit `Compile`/`CompileFile`/`Run`/`RunScript`,
> Sandbox als Voreinstellung, `examples/embedded-host/`. Zwei Befunde beim Bauen — die Runtime-
> Ausnahmen mussten an der Host-Grenze übersetzt werden (ein Host referenziert `lyrrt` nicht und
> könnte sie sonst nur pauschal fangen), und der Beispiel-Host musste in die Solution, weil
> `dotnet test` ihn sonst kalt nicht baut.

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

### ADR-026 — Ein Host-Objekt gehört dem GC, nicht dem Host

**Datum**: 2026-08-11. **Status**: Akzeptiert.

**Entscheidung**: Ein über `RegisterType<T>` sichtbar gemachtes Host-Objekt ist zur Laufzeit eine
**direkte .NET-Referenz** in `LyrValue.Ref`. Es lebt, solange ein Lyric-Wert es erreicht; der
.NET-GC sammelt es ein. **Es gibt kein Widerrufs-, Freigabe- oder Refcount-Protokoll** an der
Host-Grenze.

**Was das nicht heißt**: dass ein Host die Lebenszeit seiner Domänen-Objekte nicht steuern könnte.
Er kann — nur nicht über den Speicher. Siehe „Der Zombie-Fall" unten.

---

**Die Frage sind eigentlich zwei**: *was ist der Wert zur Laufzeit*, und *wer darf sein Leben
beenden*.

**Zur Darstellung.** Ein Index als gewöhnlicher `int` ist **verworfen**, und zwar sofort: ein
Skript kann rechnen. `spawn("goblin") + 1` erreichte ein Objekt, das ihm nie gegeben wurde. In
einer Sprache, deren erklärter Zweck die Sandbox ist (ADR-007), ist das kein Kompromiss, sondern
ein Loch. Zwischen einer direkten Referenz und einem Index *hinter einem opaken Typ* entscheidet
die Sicherheit dagegen nicht — beide sind unfälschbar. Es entscheidet die Eigentümerschaft.

**Zur Eigentümerschaft.** `CONTRIBUTING.md` Rule 2 nennt für *Memory management* genau einen
Mechanismus: **„GC only (no manual/borrow/refcount)"**. Ein host-eigenes Lebenszeitmodell stellt
daneben ein zweites: der Host entscheidet über Freigabe, das Skript muss tote Handles aushalten,
und jede Host-Methode bekommt einen Gültigkeitszweig. Das ist dieselbe Parallelität, mit der
`Result<T, E>` neben den Exceptions abgelehnt wurde — nur an der Grenze statt in der Sprache.

**Die Branchenerfahrung zeigt in dieselbe Richtung**, ungewöhnlich eindeutig:

| Einbettung | Modell | Ruf |
|---|---|---|
| Lua (`userdata`) | GC-eigen, `__gc`-Finalizer | die Einbettung, die Leute erfolgreich machen |
| Wren (foreign class) | GC-eigen, Finalizer-Callback | dito — und Lyrics erklärtes Vorbild |
| CPython C-API | Host zählt Referenzen | berühmt für genau einen Fehler |
| JNI (global refs) | Host gibt frei | dito |

§Projekt-Identität nennt **Lua und Wren** als Embedding-Vorbild. Beide sind GC-eigen.

**Der Zombie-Fall**, das ernstzunehmende Gegenargument: eine Engine zerstört eine Entity, das
Skript hält sie noch und sieht ein Objekt, das die Spielwelt vergessen hat. Das ist real — aber es
ist eine Frage der **Domänen**-Lebenszeit, nicht der Speicher-Lebenszeit, und der Host beantwortet
sie mit dem, was E3 ihm bereits gibt:

```csharp
vm.RegisterFunction("isAlive", (Entity e) => world.Contains(e));
```

Sie in die API zu heben hieße, das Lebenszeitmodell *einer* Sorte Host in die Sprache zu bauen.
Ein Editor-Plugin und ein Build-Werkzeug haben es nicht.

**Konsequenz — was es kostet.** Ein Skript, das Host-Objekte in Modul-`let`s ablegt, hält sie für
die Lebensdauer der `ScriptInstance` fest. Der einzige Hebel des Hosts ist, die Instanz
fallenzulassen; dann sammelt der GC alles daran ein. Das ist wenig Kontrolle — aber es ist eine,
und sie ist ohne Handbuch verständlich.

**Konsequenz — was es einbringt**, und das ist mehr als Bequemlichkeit: ein Zyklus über die Grenze
(Host-Objekt hält Callback hält `ScriptInstance` hält Host-Objekt) wird eingesammelt. Unter
Refcounting wäre er ein garantiertes Leck. Das ist derselbe Grund, aus dem ADR-002 den .NET-GC
genommen hat, eine Ebene höher.

**Konsequenz — die Pflicht, die daraus für E4 folgt.** Es muss garantiert sein, dass gegen einen
Host-Typ **nie ein `ldfld` emittiert wird**. Ein `Ref`, das kein `LyrValue[]` ist, überlebt jedes
Kopieren und Weiterreichen — genau wie ein `string`, der dort schon immer liegt —, aber ein
Feldzugriff casted und stürzt ab. Das ist eine Sema-Zusage („ein Host-Typ hat keine Felder") plus
ein Verifier-Test. **Dort kann E4 falsch werden, nicht bei der Lebenszeit.**

**Revidierbarkeit**: Ein Widerrufsprotokoll später hinzuzufügen bricht keinen bestehenden Host —
es wäre eine zusätzliche Zusage. Die Gegenrichtung gilt nicht: wer heute freigeben darf, verlässt
sich darauf. Deshalb ist „GC-eigen, ohne Widerruf" hier die Entscheidung, die sich revidieren
lässt.

---

### ADR-025 — Modul-Bindungen sind unveränderlich

**Datum**: 2026-08-09. **Status**: Akzeptiert.

**Entscheidung**: Auf Modulebene gibt es nur `let`, kein `var` (`LYR-PAR0027`). Der **Name** einer
globalen Bindung lässt sich nicht neu binden.

**Was das nicht heißt**: dass es keinen veränderlichen globalen Zustand gäbe. Gemessen am
2026-08-09:

```lyr
let zahlen = [1, 2, 3];
let z = Zaehler { };

fn main(): int {
    zahlen[0] = 99;      // geht
    z.stand = 42;        // geht
    return 0;
}
```

Beides ist gültig — die Regel folgt ADR-020: `let` bindet den Namen, nicht den Inhalt. Wer einen
veränderlichen Zähler braucht, schreibt sich einen Wrapper und ändert dessen Feld.

**Begründung**: Bei ADR-020 und ADR-023 wurde je eine Regel gestrichen, weil sie inkonsistent
war — sie verbot eine Schreibweise und erlaubte die gleichwertige daneben. **Diese Regel ist
anders gelagert**, und der Unterschied ist der Grund, warum sie bleibt: sie gilt ausnahmslos, und
der Ausweg ist kein Schlupfloch, sondern ein anderer Mechanismus.

Drei Gründe:

1. **Sichtbarkeit am Verwendungsort.** `n.stand = 5` sagt, dass hier ein geteiltes Objekt geändert
   wird. `n = 5` sieht aus wie eine gewöhnliche Zuweisung; man muss die Deklaration suchen, um zu
   merken, dass sie global ist. Für eine Sprache, deren erklärtes Ziel Einbettung in fremde Hosts
   ist (Game-Engines, Editoren), ist das kein Stilargument.

2. **M10 macht es scharf.** Hot-Reload muss beantworten, was beim Neuladen mit dem Wert geschieht.
   Bei `let` ist die Antwort trivial: der Initialisierer läuft neu. Bei `var` wäre sie eine echte
   Designentscheidung — und sie fiele mitten in einen Meilenstein, der genug eigene hat.

3. **Die REPL zahlt schon dafür** (ADR-021): der Initialisierer einer Deklaration läuft bei jeder
   Eingabe neu. Mit `var` wäre dieses Verhalten noch schwerer zu erklären, weil dann eine
   *Zuweisung* aus einer früheren Eingabe verlorenginge.

**Konsequenz**: Die Infrastruktur für das Gegenteil ist vollständig vorhanden — `StoreGlobal`
existiert in IR und VM, das Verbot sitzt in einer einzigen Parser-Zeile. Es später aufzuheben
bricht keinen bestehenden Code; die Gegenrichtung gilt nicht. Deshalb ist „vorerst verbieten" hier
die einzige Entscheidung, die sich revidieren lässt.

Der Preis ist Zeremonie: für einen Zähler eine Klasse zu deklarieren ist mehr Rauschen als
`var n = 0`. Das ist bewusst in Kauf genommen und der Punkt, an dem eine spätere Revision ansetzen
würde.

**Dieses ADR entstand nachträglich.** Die Regel galt seit P5b, stand aber nur als Klammerkommentar
in der Grammatik (`(* nur let, nicht var *)`) und als Parser-Meldung — ohne Begründung an einer
Stelle, an der jemand sie findet. Genau dieser Zustand hat in diesem Projekt dreimal dazu geführt,
dass eine Regel überlebte, die niemand mehr begründen konnte.

---

### ADR-024 — `Equatable` und `Hashable` sind Interfaces in `std.core`

**Datum**: 2026-08-07. **Status**: Akzeptiert.

**Entscheidung**: Gleichheit und Hash für Nutzertypen laufen über zwei Interfaces in `std.core`,
nach demselben Muster wie das bestehende `Display`: die Builtins erfüllen sie über `extend`,
Nutzertypen über `::`. `Map<K, V>` und `Set<T>` verlangen `K :: [Hashable]` als Constraint.

> **Korrektur (2026-08-07, beim Bau von S3).** Die erste Fassung dieses ADR schrieb die
> Signaturen so:
>
> ```
> pub interface Equatable { fn equals(other: Equatable): bool; }
> pub interface Hashable :: [Equatable] { fn hash(): int; }
> ```
>
> **Beides ist nicht baubar**, und das war ohne Messung erkennbar:
>
> - `other: Equatable` verlangt einen Interface-**Wert**. `std/core.lyr` sagt im Kommentar zu
>   `Display` ausdrücklich, dass ein Skalar das nicht sein kann — ein Fat Pointer braucht eine
>   Referenz. `extend int :: [Equatable]` wäre damit unmöglich, also gäbe es kein `Map<int, V>`.
>   Das Beispiel in `Sprache.md` §5.1 benutzt den **konkreten** Typ (`fn equals(other: Vector3)`)
>   und war die ganze Zeit richtiger als dieses ADR.
> - `interface Hashable :: [Equatable]` — **Interface-Vererbung gibt es in Lyric nicht.** Die
>   Grammatik (`InterfaceDecl`, §7) sieht keine Konformanzliste vor. Der Parser lehnt es ab.
>
> Was gemessen wurde und **geht**: `interface Eq<T> { fn eq(other: T): bool; }` mit
> `extend int :: [Eq<int>]` und `struct P :: [Eq<P>]`, direkt gerufen.
>
> Was **nicht** geht: `fn same<T :: [Eq<T>]>(a: T, b: T)` scheitert mit `cannot assign 'T' to 'T'`
> — der Constraint bringt sein eigenes Typargument mit, und die Substitution über die
> Constraint-Grenze fehlt. Das ist der als „Generics-Rest aus M4" notierte offene Punkt. **Ohne
> ihn ist dieses ADR nicht in brauchbarer Form umsetzbar**, denn `Map<K :: [Hashable<K>], V>` ist
> genau diese Konstruktion.
>
> Die Entscheidung selbst — Interfaces statt eingebautem Hash — bleibt. Die Signaturen werden
> festgelegt, wenn der M4-Rest steht.

**Begründung**: Das Muster steht bereits und funktioniert. `Display` ist seit M8/S1 genau so
gebaut — Interface in `std.core`, `extend int :: [Display]` für jeden Builtin, Constraint an der
Nutzung (`println<T :: [Display]>`). Ein zweiter Mechanismus für dieselbe Frage („wie liefert ein
Typ eine Eigenschaft, die eine Collection braucht?") wäre genau die Sorte Parallelität, an der
Oil gescheitert ist.

Ein **eingebauter struktureller Hash** in der VM wäre die Alternative und ist verworfen: er
verlangt, dass die VM Typ-Layouts kennt und rekursiv abläuft — dieselbe Kopplung, die ADR-013
vermeidet, weil ein Wert im Bytecode kein Typ-Tag trägt. Er würde außerdem für Klassen die
falsche Antwort geben (Identität statt Inhalt oder umgekehrt), ohne dass der Autor mitreden kann.

Dass `Hashable` von `Equatable` erbt, ist keine Bequemlichkeit, sondern die Invariante jeder
Hash-Tabelle: gleiche Werte müssen denselben Hash haben. Wer nur `hash` liefert, ohne `equals`,
baut eine Tabelle, die Kollisionen nicht auflösen kann.

**Konsequenz**: `Sprache.md` §5.1 und `Doku.md` §16.1 zeigen heute `Equatable` bzw. `Comparable`
in Beispielen, **ohne dass es beide gibt** — Spec-Beispiele ohne Implementierung. `Equatable`
entsteht mit diesem ADR. `Comparable` bleibt offen und muss entweder gebaut oder aus dem Beispiel
entfernt werden; ein Beispiel, das nicht übersetzt, ist eine Lüge in der Spec.

---

### ADR-023 — `let` und Parameter binden auch bei Structs nur den Namen

**Datum**: 2026-08-07. **Status**: Akzeptiert.

**Entscheidung**: Struct-Felder sind schreibbar wie `class`-Felder. `LYR-SEM0019` fällt für
`let`-gebundene Structs **und** für Struct-Parameter weg. Beim Parameter wirkt die Änderung auf
der **Kopie** — an der Wert-Semantik aus ADR-006 ändert sich nichts. Was bleibt: `this.feld` in
einer Methode ohne `mut` ist weiterhin verboten.

**Begründung**: Dasselbe Argument wie ADR-020, eine Ebene tiefer. Gemessen am 2026-08-07:

| Code | vorher | Wirkung der erlaubten Form |
|---|---|---|
| `fn f(p: P) { p.x = 99; }` | **`LYR-SEM0019`** | — |
| `fn f(p: P) { p.shift(99); }` mit `mut fn` | erlaubt | ändert die Kopie, folgenlos |
| `let p = …; p.x = 9;` | **`LYR-SEM0019`** | — |
| `let p = …; p.shift(9);` mit `mut fn` | erlaubt | **ändert `p` wirklich** |

Die letzte Zeile ist der Grund, warum dieses ADR über Parameter hinausgeht. Beim Parameter sind
beide Formen gleich folgenlos, das Verbot also bloß lästig. Beim `let` war es **wirkungslos**:
die `mut fn` änderte den Wert durch das `let` hindurch, während die direkte Zuweisung daneben
abgelehnt wurde. Verboten war beide Male genau die Schreibweise, die sich durch eine
gleichwertige ersetzen lässt — wortwörtlich die Begründung, mit der ADR-020 einen Tag zuvor
dieselbe Konstruktion bei Referenztypen gestrichen hat.

Damit gilt **eine** Regel für alle Typen: `let` verhindert die Neubindung des Namens, sonst
nichts.

Die Gegenrichtung — `mut fn` auf einem Nicht-`mut`-Empfänger ebenfalls verbieten — wäre ehrlich,
verlangt aber ein `mut` an Parametern und `let`-Bindungen, das die Grammatik nicht kennt.

**Konsequenz**: Die Tabelle in ADR-020 behauptete, `let p = P { hp = 1 }; p.hp = 9;` sei erlaubt
gewesen. Beim Nachmessen für dieses ADR war es `LYR-SEM0019` — die Zeile war falsch und ist als
solche markiert. Ein ADR, dessen Begründung auf einer ungemessenen Zeile steht, ist die Sorte
Fehler, die sich still fortpflanzt.

---

### ADR-022 — `char` ist ein Ganzzahltyp mit geprüftem Wertebereich

**Datum**: 2026-08-07. **Status**: Akzeptiert.

**Entscheidung**: `char` zählt zur Numerik (§6.5). Damit gehen `c as int`, `n as char`, `c < 'z'`,
`c + 1` und die bitweisen Operatoren. **Jede Operation, die ein `char` erzeugt, prüft das
Ergebnis**: ein Wert jenseits `0x10FFFF` oder im Surrogate-Bereich `D800–DFFF` ist ein `panic`,
kein stiller Wert.

**Begründung**: Der Anlass ist `std.string`. Alles, was Zeichen klassifiziert oder Zahlen parst —
`isDigit`, `toUpper`, ein Ziffernwert — ist Codepoint-Arithmetik, und ohne einen Weg dorthin
müsste jede dieser Funktionen nativ sein. Eine Standardbibliothek, die für „ist das eine Ziffer?"
in den Host absteigt, gibt zu, dass die Sprache zu wenig kann.

Die **Prüfung** ist der Preis dafür, dass §4 wahr bleibt: dort steht „`char` = ein Unicode-
Codepoint", und ein Typ, dessen Zusage man durch Addition brechen kann, macht die Zeile zur
Dekoration. Die Alternative — nicht prüfen — wäre schneller und ist verworfen: ein ungültiger
Codepoint fällt sonst erst beim Drucken auf, weit entfernt von der Rechnung, die ihn erzeugt hat.

**Konsequenz**: Weil Numerik **strikt** ist (§6.5: beide Seiten derselbe Typ), ist `c + n` mit
`n: int` weiterhin ein Fehler und braucht `c as int + n`. Das ist konsistent mit `int8 + int` und
wird trotzdem die häufigste Überraschung dieser Änderung sein.

`'a' * 1000` ist ab jetzt ein wohlgeformter Ausdruck, der zur Laufzeit panict. Die Sprache lässt
Unsinn zu, den sie vorher syntaktisch verhindert hat — der bewusste Preis dafür, dass `c + 1`
ohne Umweg geht.

---

### ADR-021 — Die REPL ist ein eigenes Werkzeug

**Datum**: 2026-08-07. **Status**: Akzeptiert.

**Entscheidung**: `lyrrepl` ist ein viertes Binary neben `lyrc`, `lyrvm` und `lyric`. Der Treiber
ruft es über denselben Mechanismus wie die anderen (`--repl`, `LYRIC_REPL`); `lyric repl` ist ein
Dispatch, kein eigener Code.

**Begründung**: Die REPL ist das erste Werkzeug, das **Frontend und Runtime im selben Prozess**
braucht — jede Eingabe wird übersetzt *und* ausgeführt, und der Zustand muss dazwischen leben.
`lyric run` löst das heute über zwei Subprozesse; für eine REPL geht das nicht.

Käme sie in den Treiber, hätte der wieder beide Seiten. Genau das hat **ADR-019** abgeschafft:
*„Der Treiber hat jetzt genau eine Referenz: `Lyric.Core`. … `lyric` war keine vereinfachte
Oberfläche, sondern eine zweite Implementierung."* Der Architektur-Test, der das festhält, würde
fallen — und **dass er fällt, wäre die Aussage**, so wie sein Umdrehen damals die Entscheidung war.

ADR-019 sieht den Fall ausdrücklich vor: *„`lyrtest` fügt sich als drittes Werkzeug ein, ohne dass
am Dispatcher etwas zu ändern wäre; das ist der Test dafür, ob dieser Entwurf trägt."* `lyrrepl`
ist dieser Test, nur früher und mit einer schärferen Anforderung.

**Konsequenz**: `lyrrepl` ist das erste Binary mit **beiden** Bibliotheken. Das widerspricht
ADR-017 nicht — die Kante trennt die Bibliotheken, sie verbietet nicht, beide zu benutzen. Dass
man sie kombinieren kann, ohne sie aufzuweichen, ist der Beweis, dass der Schnitt sauber liegt.
Der Architektur-Test bekommt einen vierten Fall, der das ausdrücklich erlaubt; das
Auslieferungsverzeichnis wächst von 13 auf 14 Einträge.

**Zum Zustand zwischen Eingaben**: Deklarationen (`fn`, `class`, `struct`, `enum`, Modul-`let`)
sammeln sich an und werden bei jeder Eingabe mitübersetzt; **Statements laufen nur einmal**, als
Rumpf eines synthetischen `main`. Damit druckt ein `println` aus Eingabe 3 nicht bei Eingabe 4
erneut — der Fehler, den eine REPL macht, die schlicht den ganzen Quelltext akkumuliert.

Der Preis, ausgesprochen: der *Initialisierer* einer Deklaration läuft bei jeder Eingabe neu. Bei
`let x = 5` ist das unsichtbar, bei `let s = readText(…)` nicht. Ein Wert, der wirklich einmal
berechnet wird, bräuchte persistente Globals in der VM — formatneutral nachrüstbar, und die
Trennung Deklaration/Statement bleibt dabei unverändert.

---

### ADR-020 — `let` bindet den Namen, nicht den Inhalt

**Datum**: 2026-08-07. **Status**: Akzeptiert.

**Entscheidung**: Bei einem Referenztyp verhindert `let` ausschließlich die **Neubindung des
Namens**. Der Inhalt dahinter bleibt änderbar. `let xs = [1, 2]; xs[0] = 9;` ist damit gültig —
bisher war es `LYR-SEM0019`. Die Zeile „Container muss mut sein" fällt aus `Sprache.md` §6.4.

**Begründung**: Die alte Regel war nicht nur inkonsistent, sie war **wirkungslos**. Drei Fälle,
gemessen:

| Code | vorher |
|---|---|
| `let p = P { hp = 1 }; p.hp = 9;` | erlaubt *(falsch — war `LYR-SEM0019`; nachgemessen bei ADR-023)* |
| `let xs = [1, 2]; xs[0] = 9;` | **`LYR-SEM0019`** |
| `let ps = [P { hp = 1 }]; ps[0].hp = 9;` | erlaubt |

Der dritte Fall entwertet den zweiten: über ein `let`-Array hindurch ließ sich der Inhalt eines
Elements ändern. Verboten war genau die eine Operation, die man umgehen konnte, indem man ein
Element mit einem Feld nahm. Eine Regel, die nichts schützt und dafür zwei Referenztypen
verschieden behandelt, kostet nur Erklärungsaufwand.

Seit **ADR-016** ist `T[]` ein echter Referenztyp wie eine Klasse — spätestens damit war die
Sonderbehandlung nicht mehr begründbar. Java (`final`), C# (`readonly`) und JavaScript (`const`)
machen es alle so: der Name ist fest, das Objekt nicht. **Rust ist die einzige Sprache, die
Unveränderlichkeit bis in den Inhalt durchhält, und sie kann das nur wegen Ownership und
Borrowing** — beides hat Lyric nicht und will es laut ADR-003 auch nicht.

**Konsequenz**: `IsMutableLvalue` behandelt ein Array-Element wie ein Klassenfeld. `Indexable<T>`
(M8/S5) bekommt einen gewöhnlichen `mut fn`-Setter, ohne eine Sonderregel nachbilden zu müssen —
das war der Anlass, die Frage jetzt zu entscheiden statt sie ein drittes Mal zu vertagen.

Was Lyric damit **nicht** hat: eine Möglichkeit, ein unveränderliches Array auszudrücken. Sie
hat faktisch nie existiert (siehe Fall 3). Wer sie später will, braucht einen eigenen Typ
(`Frozen<T>` o. ä.) und keine Bindungs-Annotation — das ist eine Bibliotheks-, keine
Sprachfrage.

---

### ADR-019 — `lyric` ist ein Dispatcher, kein zweiter Compiler

**Datum**: 2026-08-06. **Status**: Akzeptiert. **Revidiert**: ADR-017 (In-Process-Ausführung).

**Entscheidung**: `lyric` übersetzt nichts und führt nichts aus. Es wählt Werkzeuge, übersetzt
bequeme Kommandos in technische und reicht durch, was zurückkommt: `lyric run app.lyr` ist
`lyrc build` gefolgt von `lyrvm run`, mit einer temporären Datei dazwischen. Damit hat der Treiber
keine Referenz mehr auf `lyrfe` oder `lyrrt` — neben `lyric.exe` liegen die *Werkzeuge*, nicht
deren Bibliotheken.

Die Werkzeug-Auflösung ist für alle gleich und gestaffelt: `--<flag> <pfad>` schlägt
`LYRIC_<TOOL>` schlägt „neben der eigenen exe". Vorher galt sie nur für die Runtime.

**Begründung**: ADR-017 ließ die mitgelieferte Runtime in-process laufen und begründete das mit
einem gesparten Prozessstart von „~50–70 ms". **Gemessen am 2026-08-06 hält das nicht**:

| | |
|---|---|
| `lyric run` in-process | ~283 ms |
| `lyric run --vm lyrvm.exe` (Subprozess) | ~290 ms |
| nackter Prozessstart (`lyrvm --version`) | ~120 ms |

Der Unterschied liegt im Rauschen. Bezahlt wurde er mit zwei Ausführungspfaden, die gegeneinander
getestet werden mussten (ADR-017 nannte das selbst „den Preis der In-Process-Entscheidung"), und
mit vier Kommandos — `run`, `build`, `check`, `disasm` —, die es zweimal gab. `lyric` war keine
vereinfachte Oberfläche, sondern eine zweite Implementierung derselben Sache.

Dass **alle** Werkzeuge austauschbar sind und nicht nur die Runtime, folgt aus der Absicht, die
Sprache nach v1 formell zu spezifizieren: dann ist ein zweiter Compiler genauso denkbar, wie
ADR-013 heute eine zweite Runtime denkbar macht. Ein Sonderweg nur für die Runtime wäre jetzt
bequemer und später im Weg.

Das ist das `git`-Modell (ein Dispatcher startet `git-<subcommand>`), das `cargo`-Modell (ruft
`rustc`) und das `dotnet`-Modell (ruft MSBuild). Alle drei zahlen den Prozessstart und bekommen
dafür Werkzeuge, die einzeln benutzbar, einzeln testbar und einzeln ersetzbar sind. Das
Gegenmodell — ein Monolith mit Symlink-Namen wie BusyBox — spart den Start und macht genau diese
Austauschbarkeit unmöglich.

**Konsequenz**: Der Zwei-Pfade-Test `In_process_and_foreign_vm_paths_agree` wird
**gegenstandslos**, nicht gestrichen — was er absicherte, kann nicht mehr auseinanderlaufen. Der
Architektur-Test dreht sich um: wo vorher stand „der Treiber *muss* beide Seiten haben", steht
jetzt „er hat genau eine Referenz, und die heißt `Lyric.Core`". — Die Werkzeuge werden beim Build
vollständig neben den Treiber kopiert, damit „neben sich" während der Entwicklung dasselbe heißt
wie im Publish-Verzeichnis; ein framework-abhängiges `lyrc.exe` ist ein Launcher und braucht seine
`lyrc.dll` daneben. — `lyric run` ruft den Compiler mit `--quiet`: wer `run` tippt, will sein
Programm sehen und nicht die Größe eines Zwischenartefakts, das gleich wieder verschwindet. Das
ist die Sorte Vorgabe, für die dieser Einstiegspunkt existiert. — Das Zwischenartefakt ist eine
temporäre Datei und kein Cache: ein Cache bräuchte ein Verzeichnis, eine Invalidierungsregel (die
auch Stdlib-Änderungen erfassen muss) und ein `clean`, also einen eigenen Mechanismus mit einer
eigenen klassischen Fehlerquelle. Er lässt sich später darüberlegen, ohne den Entwurf zu ändern.
— **Offen**: `lyrtest` (post-v1) fügt sich als drittes Werkzeug ein, ohne dass am Dispatcher etwas
zu ändern wäre; das ist der Test dafür, ob dieser Entwurf trägt.

---

### ADR-018 — Closures fangen Variablen, nicht Werte

**Datum**: 2026-08-06. **Status**: Akzeptiert.

**Entscheidung**: Eine Closure fängt die **Variable**, nicht ihren Wert. Schreibt sie in ein
gefangenes `var`, sieht die umgebende Funktion die Änderung, und umgekehrt — auch dann noch, wenn
der erzeugende Aufruf längst zurückgekehrt ist. Umgesetzt wird das, indem ein gefangenes `var` in
einer **Zelle auf dem Heap** lebt statt in einem Frame-Slot; alle Zugriffe darauf, innerhalb wie
außerhalb der Closure, gehen über die Zelle.

`let`-Bindungen und Parameter werden dagegen **kopiert**. Für sie ist der Unterschied nicht
beobachtbar: sie ändern sich nie (Zuweisung an einen Parameter ist `LYR-SEM0019`), und die Kopie
ist billiger. Das ist keine zweite Semantik, sondern dieselbe — nur ohne die Zelle, die niemand
lesen würde.

```lyr
fn counter(): fn() -> int {
    var n = 0;                       // lebt in einer Zelle, nicht im Frame
    return () => { n += 1; return n; };
}

let next = counter();
next();                              // 1
next();                              // 2 — dieselbe Zelle
```

**Begründung**: ADR-011 begründet *implizite* Captures damit, dass C#-Nutzer sie erwarten. Wer
implizite Captures erwartet, erwartet auch C#s Capture-*Semantik*; still etwas anderes zu tun wäre
die schlechtere Hälfte der Übernahme. Die Alternative — gefangene `var` verbieten, wie Javas
„effectively final" — wäre billiger zu bauen und lehnt den Fall wenigstens ehrlich ab, macht aber
genau das unmöglich, wofür Game-Scripting Closures benutzt: ein Handler, der Zustand fortschreibt.
Die dritte Möglichkeit, `var` beim Erzeugen einzufrieren (C++ `[=]`), ist die einzige, die still
etwas anderes tut als jeder Leser annimmt; sie fällt aus demselben Grund weg wie stille
Truncation bei Casts.

**Konsequenz**: Das Lowering muss **alle** Zugriffe auf ein gefangenes `var` umschreiben, auch die
in der umgebenden Funktion — sonst sähen beide Seiten verschiedene Werte. Die Sema markiert die
betroffenen Symbole beim Erfassen der Captures (`TypeResult.IsBoxed`), das Lowering fragt an jeder
Zugriffsstelle nach. Eine Zelle ist dabei **kein neuer Mechanismus**: sie ist ein Objekt mit einem
Feld, also `newobj` + `ldfld`/`stfld`, und der Verifier prüft sie wie jedes andere Objekt. — Der
Preis ist GC-Druck bei häufig erzeugten Closures; ADR-011 nennt dafür bereits `@noCapture` als
post-v1-Option. — Nicht entschieden und hier nicht nötig: ob Lua-artige „Upvalues" (Zelle erst beim
Verlassen des Frames schließen) später Speicher sparen sollen. Das wäre eine reine
Repräsentations-Änderung hinter derselben Semantik.

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

### ADR-017 — Drei Binaries: `lyrc`, `lyrvm`, `lyric` — plus Runner-Vertrag

**Datum**: 2026-08-05. **Status**: Akzeptiert.

**Entscheidung**: Die Toolchain liefert **drei** ausführbare Dateien statt einer.

- **`lyrc`** — der Compiler. Technische Oberfläche, ein Job pro Aufruf: `build`, `check`, plus die
  Debug-Dumps `tokenize`, `parse`, `lower`. Kennt die VM nicht.
- **`lyrvm`** — die mitgelieferte Runtime. Kennt ausschließlich `.lyrbc`: `run`, `disasm`, `verify`.
  `lyrvm run` auf einer `.lyr`-Datei ist ein Fehler, keine stille Weiterleitung.
- **`lyric`** — der Treiber. Bequeme Oberfläche, fasst Schritte zusammen (`run` auf einer Quelle
  compiliert und führt aus), reicht `build`/`check` durch und wählt über `--vm` die Runtime.

Der Treiber bekommt **keine eigene Compile- oder Ausführungslogik**: er ruft dieselben
Bibliotheks-Einstiege wie `lyrc` und `lyrvm`. Die Debug-Dumps bekommt er **nicht** — sie sind
Compiler-Interna.

Dazu die Abhängigkeitsregel, ohne die der Split kosmetisch wäre: **`lyrvm` referenziert nichts
Compiler-seitiges.** Weder Lexer, Parser, Resolver, Sema, IR noch den Bytecode-*Writer*. Dafür
zerfällt `Lyric.Bytecode` in seine zwei Richtungen: die Leseseite (Format, `BytecodeModule`, Reader,
`CodeDecoder`, Disassembler, Encoder) hängt nur an `Lyric.Core`; die Schreibseite (`BytecodeWriter`,
`StackScheduler`) zieht als `Lyric.Bytecode.Emit` um und hängt an `Lyric.Ir`.

Schließlich der **Runner-Vertrag** (normativ in `docs/Bytecode.md`), damit „austauschbare Runtime"
keine Behauptung ist. Er hat vier Punkte und sonst nichts:

1. Aufruf: `<vm> run <datei.lyrbc> [-- <programm-args>]`
2. Exit-Codes: Rückgabewert von `main` maskiert mit `& 0xFF`; `101` = panic; `1` = Lade-,
   Validierungs- oder IO-Fehler; `2` = Aufruf-Fehler.
3. Ströme: stdout ist Programmausgabe, stderr ist Diagnose und Backtrace. Keine Vermischung.
4. `<vm> --version` liefert freien Text, den `lyric` durchreicht und nie interpretiert.

Ausgewählt wird eine Runtime über `--vm <pfad>`, sonst `LYRIC_VM`, sonst die mitgelieferte — Flag
schlägt Umgebungsvariable, dieselbe Staffelung wie beim bereits existierenden `LYRIC_STDLIB`. Mit
der mitgelieferten VM läuft `lyric run` **in-process**; nur eine Fremd-VM wird als Subprozess
gestartet (sie braucht dann eine materialisierte Datei statt In-Memory-Bytes).

**Begründung**: Der Auslöser ist ADR-013. Wer `.lyrbc` zum spezifizierten, plattformneutralen
Vertrag erklärt, dessen Ziel-Test lautet „jemand schreibt eine zweite Runtime, ohne den C#-Code zu
lesen" — und muss dann auch selbst eine Runtime bauen können, die den Compiler nicht enthält. Heute
ist das nicht der Fall: `Lyric.Bytecode` referenziert `Lyric.Ir`, das `Lyric.Sema` referenziert, und
damit zieht `Lyric.Vm` die **gesamte** Front-End-Kette mit. Ein Binary-Split ohne diesen Schnitt
wären drei Namen auf einem Monolithen. Gemessen am 2026-08-05 benutzen von der Leseseite genau
**null** Dateien die IR; nur `BytecodeWriter` und `StackScheduler` tun es. Der Schnitt ist mechanisch
— und dass er es ist, ist das Verdienst der P5-Entscheidung, `BytecodeModule` bewusst nicht als
`IrModule` zu modellieren. — Die Trennung von Lese- und Schreibseite ist kein exotischer Zuschnitt:
in .NET liest `System.Reflection.Metadata` und schreibt `Microsoft.CodeAnalysis`, und wer IL nur
*ausführen* will, linkt den Emitter nicht. — Für die Dreiteilung selbst ist **nicht** `dotnet` das
Vorbild, obwohl es der Anlass war: `dotnet` ist *ein* Binary, `dotnet build` startet kein `csc.exe`,
sondern fährt MSBuild und führt `csc.dll` über `dotnet exec` aus. Was es tatsächlich vormacht, ist
Treiber-Binary **plus separat aufrufbare Werkzeuge**, wobei der häufige Pfad in-process bleibt. Näher
liegt `rustc`/`cargo`: roh und bequem als zwei Namen, wobei `cargo` nie eigene Codegen-Logik bekam.
Der Unterschied zu Rust ist die VM — daher der dritte Name — und die Zeitskala: Cargo darf `rustc`
als Prozess starten, weil Kompilieren in Sekunden misst; `lyric run` misst in Millisekunden, und ein
zweiter .NET-Prozessstart (~50–70 ms) auf dem häufigsten Kommando einer Sprache für CLI-Tools und
Game-Scripting ist der falsche Preis. Deshalb in-process als Default und Subprozess nur, wo er
unvermeidbar ist. — Der Vertrag ist bewusst **vierzeilig**. Der naheliegende fünfte Punkt wäre ein
Capability-Probe (`<vm> --lyrbc-versions`), damit der Treiber vorab „deine VM kann nur 1.4, dieses
Modul ist 2.0" sagen kann. Er entfällt: ADR-013 verlangt bereits, dass jede Runtime unbekannte
Major-Versionen **beim Laden** ablehnt, und `NativeRegistry.Bind` prüft Import-Namen *und*
Signaturen zur selben Zeit. Die Fremd-VM liefert die präzise Meldung also von allein; ein Probe wäre
ein zweiter Kompatibilitäts-Mechanismus neben der Load-Zeit-Validierung (Rule 2) — und der teurere,
weil ihn jede Fremd-VM nachbauen müsste. Aus demselben Grund gibt es **kein** VM-Registry
(`lyric vm add …`): das bedeutete persistente Konfiguration, also eine Konfigurationsdatei, also
Projektdateien, also ein Projektsystem. Ein Pfad in einer Variablen erledigt denselben Job zustandsfrei.

> **Teilweise revidiert durch ADR-019 (2026-08-06)**: die In-Process-Ausführung der
> mitgelieferten Runtime ist gestrichen — ihre Begründung („spart ~50–70 ms") hielt der
> Messung nicht stand. Der Rest dieses ADR gilt unverändert: drei Binaries, der
> Runner-Vertrag, die Abhängigkeitsregel.

**Konsequenz**: Zwei neue Bibliotheken (`Lyric.Bytecode.Emit`, `Lyric.Compiler`) und zwei neue
Executables; `Lyric.Cli` bleibt und wird zum Treiber. `Lyric.Compiler` ist dabei kein Vorgriff — M10
verlangt `LangVm.Compile(string source)`, und das *ist* diese Bibliothek; sie entsteht hier nur
früher. Die Exit-Code-Regel wandert als `VmHost` in `Lyric.Vm`, weil sie normativ ist (§9/§11) und
über alle Runtimes identisch sein muss — sie gehört in die Referenz-Runtime, nicht in ein
CLI-Projekt. — Zwei Ausführungspfade im Treiber (in-process und Subprozess) sind der Preis der
In-Process-Entscheidung; sie werden mit derselben Beispiel-Matrix gegeneinander getestet, wobei das
mitgelieferte `lyrvm` als per Konstruktion vertragskonformes Testdouble für „Fremd-VM" dient. Ohne
diesen Test wäre der Zwei-Pfade-Entwurf nicht vertretbar. — Damit entsteht `tests/Lyric.Tests.Cli/`
und tilgt die seit M6 offene Schuld „kein CLI-Test-Projekt"; sein wichtigster Fall ist der
Architektur-Test, der die Abhängigkeitsregel maschinell festhält, weil eine zurückwandernde Kante
sonst niemandem auffiele. — `dotnet publish` muss alle drei Apphosts in **ein** Ausgabeverzeichnis
legen, sonst liegt die Runtime bei `--self-contained` dreifach vor. — `Doku.md` §23 und die
Ordnerstruktur oben werden entsprechend umgeschrieben. — Offen und hier nicht gelöst: der Vertrag
sieht `-- <programm-args>` vor, aber `fn main(args: string[])` (Sprache.md §11) ist nirgends
verdrahtet — `ModuleLowerer` nimmt nur ein parameterloses `main` als Einstieg. Argumente werden
deshalb vorerst mit einer Diagnose abgelehnt statt still verworfen.

> **Prozess-Vermerk**: Diese Entscheidung fiel außerhalb des Scope-Check-Rituals
> (`CONTRIBUTING` §Scope check), drei Tage nach dem Check vom 2026-08-02. Sie ist damit formal eine
> Impuls-Planänderung. Festgehalten wird sie trotzdem als ADR und nicht stillschweigend umgesetzt,
> weil sie den Abhängigkeitsgraphen ändert. Zwei Dinge halten die Ausnahme klein: der Split ist
> *strukturell* (er fügt der Sprache nichts hinzu und löst einen Architekturfehler auf, den ADR-013
> ohnehin verbietet), und er wird mit jedem weiteren Slice teurer, weil M9 mit `repl` und `test`
> zwei Kommandos bringt, die an den Treiber gehören. Die austauschbare Fremd-VM stammt dagegen aus
> `docs/IDEAS.md` („Native Runtime als zweite Implementierung der `.lyrbc`-Spec") und wird deshalb
> auf das Minimum beschränkt: vier Vertragszeilen und ein Flag, kein Registry, keine
> Konfigurationsdatei. *(Zur Ratifizierung im nächsten Scope-Check.)*

### Nachtrag 2026-08-06: drei Assemblies statt elf

Die Konsequenz oben nennt „zwei neue Bibliotheken" — und ließ damit offen, wie viele es am Ende
sind. Es waren elf, und alle elf lagen im Auslieferungsordner. Für eine Toolchain, die jemand
herunterlädt, ist das keine Architektur, sondern eine Zumutung: elf Dateinamen, die eine
Projektgliederung verraten, die den Benutzer nichts angeht.

Ausgeliefert werden jetzt **drei**, und die Schnitte liegen exakt auf der Kante, die dieses ADR
zieht:

| Assembly | Inhalt | Wer bekommt sie |
|---|---|---|
| `lyrcore.dll` | Diagnostik, Quelltextverwaltung, **Leseseite** des Formats | alle drei |
| `lyrfe.dll` | Lexer, Parser, Resolver, Sema, IR, **Schreibseite**, Pipeline | `lyrc`, `lyric` |
| `lyrrt.dll` | der Interpreter | `lyrvm`, `lyric` |

**Warum das Format-Lesen zu `lyrcore` gehört und nicht zur VM.** Es ist der gemeinsame *Vertrag*
und keine Runtime-Eigenschaft: `lyrvm info` liest ein Modul, ohne es auszuführen, `lyric disasm`
auch, und der Bytecode-Writer braucht dieselben Op-Codes und Typ-Tags. Läge es bei der VM, zöge
jeder Compiler-Build den Interpreter mit — die *Gegen*richtung dieses ADRs, und sie fällt genauso
auf.

**Was das kostet, ehrlich**: die feinen Kanten *innerhalb* des Frontends sind ab jetzt Konvention
statt Compilerfehler. Der Parser könnte die Sema rufen. Was bleibt — und was dieses ADR wirklich
behauptet — ist die große Kante: eine Runtime bekommt nichts vom Compiler zu sehen, ein Compiler
keinen Interpreter. Die wird weiterhin durch Assembly-Grenzen erzwungen.

**Der Architektur-Test wird dadurch schärfer.** Vorher eine Verbotsliste aus acht Dateinamen, die
bei jedem neuen Projekt hätte wachsen müssen — was niemand tat. Jetzt ein Gleichheitsvergleich:
„neben `lyrvm.exe` liegen genau `lyrcore.dll`, `lyrrt.dll`, `lyrvm.dll`". Das fällt auch über eine
Kante, die auf keiner Verbotsliste steht — und über **Build-Leichen**: beim Umbau lagen in `bin/`
noch die DLLs der elf alten Projektnamen, und die Verbotsliste hätte sie durchgewinkt.

---

## Was nach v1.0 kommt

Wir schreiben **keine** ausführliche post-v1-Roadmap (das war einer der Oil-Fehler — das Dokument verselbständigte sich). Hier nur eine sehr kurze Skizze, was *vielleicht* kommt, in vagem Prioritäts-Sortier. Konkretisierung erfolgt erst, wenn die jeweilige Phase angefangen wird.

| Phase | Inhalt | Wann |
|---|---|---|
| **v1.1** | DateTime, Regex, JSON in Stdlib. Newtypes. `pub(package)`. Raw-Strings. | wenn echter Bedarf erkennbar |
| **v1.1** | Attribute (`@test`, `@deprecated`, `@inline`, `@cold`, `@noCapture`) samt Grammatik, und `lyric test` darauf | wenn die Sprache produktiv ist |
| **v1.2** | LSP-Server (Diagnostics-Streaming, Hover, Go-to-Definition) | wenn Sprache produktiv ist |
| **v1.3** | Async/Await-Syntax als Zucker über Coroutinen | wenn Async-Code stark gefragt |
| **v1.4** | User-defined Operator-Overloading; Funktions-Overloading (ADR-015) | wenn Math-Libs schreien |
| **v1.5** | Formatter `lyric fmt` | wenn Community entsteht |
| **v1.X?** | `std.io.net` (TCP/HTTP) — setzt eine Entscheidung über blockierende I/O in einer Single-Thread-VM voraus (ADR-010) | wenn Server-Use-Case konkret wird |
| **v1.X?** | JIT-Backend (Cranelift), Package-Manager | nur bei demonstriertem Bedarf |

**Niemals** geplant:
- Class-Inheritance.
- Result-Typ als zweiter Error-Mechanismus.
- Raw-Pointer / `unsafe`.
- Eigener Debugger (.NET-Debugger reicht).
- Eigene Package-Registry (Solo-Projekt).
- Multi-Editor-Plugins jenseits VS Code (community-aufgabe).
