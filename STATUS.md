# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M4 — Sema (full) — in Arbeit**

Slice-Schnitt: **1** Generics, **2** Pattern-Match voll + Exhaustivität, **3** Exceptions
+ Coroutinen, **4** Closures + Interfaces + Extend. Entscheidungen D1–D5 bestätigt
(Monomorph / strenge Constraints / kein Turbofish / pragmatische Exhaustivität /
bidir. Lambda-Inferenz). **Slice 1a (Generics-Fundament) durch.**
M3 = `m3-complete`, M2 = `m2-complete`, M1 = `m1-complete`.

## Was schon erledigt ist

- [x] **M1 — Lexer** (`m1-complete`).
- [x] **M2 — Parser** (`m2-complete`): voller AST, RD + Pratt, Patterns/`match`/if-Ausdruck/
  Struct-Init; `AstDumper`, CLI `lyric parse`. Codes `LYR-PAR0001..0037`.
- [x] **M3 — Resolver + Sema (basic)** (`m3-complete`): Resolver (Symbole/Scopes, Imports,
  Typ-Namen-Bindung, Builtins, Zyklen), TypeChecker (jeder Ausdruck typt, Numerik strikt,
  `+`/`*` string/T[], Calls/Member/Struct-Init/if/match/Lambda), Flow (Return-Coverage, DAA,
  Narrowing), Regeln (Lvalue/Mutabilität, Konformität, `main`). CLI `lyric check`.
  Codes `LYR-RES0001..0005`, `LYR-SEM0001..0025`.
- [x] **M4 — Slice 1a — Generics-Fundament**:
  - **Resolver**: `GenericParamSymbol`; Typ-Params (Typ- und Funktions-generisch) lösen auf
    (kein `RES0002` mehr auf `T`); Member gegen Typ-Member-Scope gebunden, Constraints gebunden.
  - **Sema**: `TypeParamType` + `GenericInstance` (invariant-gleich); `Box<int>.value: T` →
    `int` per **Substitution** (rekursiv, in Array-/Tuple-/Fn-Feldern); Constraint-Member auf
    `T` nur aus Constraints (D2); Arity-Check. Codes `LYR-SEM0026/0027`. 13 Tests.

## Woran wir gerade arbeiten

**M4 — Slice 1b (Generics, Teil 2)**: generische Konstruktion `Stack<int> { }` + `TypePath`
(Grammatik §6.2/§6.3 scharf machen + Parser), Call-Inferenz (`identity(5)` → `T=int`),
Constraint-Erfüllung (`int :: Comparable`? inkl. Builtin-Conformance).

## Was als nächstes ansteht

- M4-1b (s.o.), dann Slices **2** (Pattern-Payload + Exhaustivität), **3** (Exceptions +
  Coroutinen), **4** (Closures + Interfaces + Extend-Merge + Orphan-Rule).

## Noch offen in M4 (Slice-Zuordnung)

- **1b**: Konstruktion `Stack<int> { }` + `TypePath`; Call-Inferenz; Constraint-Erfüllung;
  Constraints mit eigenen Typ-Args (`Comparable<T>` über die Constraint-Grenze substituieren).
- **2**: Enum-Payload-Destructuring (M3-Poison → echte Typen); `match`-Exhaustivität;
  Block-Wert-Frage (Block-Arme / Block-Lambdas).
- **4**: `extend`-Merge + Orphan-Rule (inkl. Extend-Methoden-Generics); Interface-Konformität
  mit Signatur-Match (nicht nur Namen).
- Extern (nicht M4): Stdlib-Imports opak → Modul-Universum erst mit M8.

## Design-Entscheidungen (Kontext)

- AST = immutable Records; Symbole = mutable Klassen; Binding/Typen via Seiten-Tabellen (Roslyn-Stil).
- Builtins als Wurzel-Scope; 2-Pass-Deklarieren; strukturierte Flow-Analyse (kein CFG).
- Typsystem-Regeln in `Sprache.md §6.5`; `ErrorType` = Poison (keine Folgefehler).
- Generics: Monomorphisierung (Sema sammelt Instanzen, Codegen → M5); strenge Constraints (D2).
- M1/M2-Kernentscheidungen: in den Tags bzw. der git-Historie.

## Letzter relevanter Commit

`M4: sema — generics foundation (slice 1a)`

---

## Wie diese Datei zu pflegen ist

- Nach jedem Slice: `## Was schon erledigt ist` ergänzen, `## Woran wir
  gerade arbeiten` updaten, ggf. `## Was als nächstes ansteht` neu sortieren.
- Bei Meilenstein-Wechsel: oben den neuen Meilenstein eintragen, alte
  abgehakte Items aus `## Was schon erledigt ist` ausdünnen (nur die
  letzten 2–3 Slices behalten, Rest ist in `git log`).
- Bei Block-/Diskussions-Bedarf: `## Offene Fragen` füllen — Claude
  greift das in der nächsten Session auf.
- **Niemals** hier neue Features planen. Das ist `ROADMAP.md`-Territorium.
