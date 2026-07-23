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
bidir. Lambda-Inferenz). **Slice 1 (Generics) und Slice 2 (Pattern-Match voll) komplett.**
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
- [x] **M4 — Slice 1 — Generics** (1a + 1b):
  - **Resolver**: `GenericParamSymbol`; Typ-Params (Typ- und Funktions-generisch) lösen auf
    (kein `RES0002` mehr auf `T`); Member gegen Typ-Member-Scope gebunden, Constraints gebunden.
  - **Sema**: `TypeParamType` + `GenericInstance` (invariant-gleich); Member-**Substitution**
    (`Box<int>.value: T` → `int`, rekursiv); Constraint-Member auf `T` nur aus Constraints (D2);
    **Konstruktion** `Box<int> { }` (Feld-Typen substituiert, Arity, keine Feld-Inferenz);
    **Call-Inferenz** (`ident(5)` → `T=int`, strukturell durch `T[]`/`?T`/Tupel/Instanzen/fn);
    **Constraint-Erfüllung** bei Konstruktion + Call (Nutzertypen via `:: [I]`-Liste, Typ-Params
    via eigene Constraints; Builtins lenient bis M8). Codes `LYR-SEM0026..0028`. 28 Tests.
  - **Parser/Grammatik**: `IsStructInitAhead` skippt balancierte `<…>` (Vergleich bleibt
    Vergleich); `StructInitExpr.TypeArguments`; `TypePath` in Sprache.md §6.2 definiert
    (Typen explizit, Funktionen inferieren, kein Turbofish).
  - `stack.lyr` damit sauber bis auf Array-Methoden (`.push/.pop/.length` → M8-Stdlib).
- [x] **M4 — Slice 2 — Pattern-Match voll**:
  - **Patterns**: Enum-Payload-Destructuring mit echten Typen (Tuple-/Struct-Varianten,
    generisch substituiert, qualifizierte Pfade gegen das Scrutinee-Enum validiert);
    Struct-/Tuple-Destructuring; Or-Pattern-Konsistenz (gleiche Namen + Typen, SEM0032);
    Literal-/Range-Patterns typgeprüft; `?T`-match: nicht-null-Arme matchen gegen `T`
    (Doku §6), ohne null-Arm nicht exhaustiv.
  - **Exhaustivität** (SEM0050, D4): Enums variantengenau (fehlende Varianten namentlich
    in der Meldung), bool über true/false, `?T` über null + Inner; offene Typen (int, …)
    brauchen `_`/Bindung; Guards zählen nicht. Exhaustive matches speisen Return-Coverage
    (kein falsches SEM0017) und DAA (Schnitt der Arm-Zuweisungen, kein falsches SEM0018).
  - **Block-Wert-Regel**: Blöcke haben keinen Wert — Block-Arm im match-**Ausdruck** muss
    auf jedem Pfad return/throw-en (SEM0033), im match-Statement frei. → Offene Fragen.
  - **Konstruktion (§3.4)**: Enum-Struct-Varianten qualifiziert (`Shape.Triangle { … }`)
    und kontextuell (`let s: Shape = Triangle { … }`, auch Array-Element/Return/Feld/
    Assign) via minimalem expected-Type-Threading in CheckExpr (Vorstufe von D5);
    leeres Array-Literal nimmt den Kontext-Typ.
  - Codes `LYR-SEM0029..0033` + `0050`. 50 Tests; `shapes.lyr` checkt sauber.

## Woran wir gerade arbeiten

Slice 2 fertig. Nächster Slice: **M4-3 — Exceptions + Coroutinen** (`throws`-Propagation,
Catch-Typ-Validierung, Throwable-Constraint SEM0030; `yield`/`resume`-Validierung,
Coroutine-Return-Typ). Noch nicht geplant.

## Was als nächstes ansteht

- M4-3 planen + bauen, dann **4** (Closures + Interfaces + Extend-Merge + Orphan-Rule).

## Noch offen in M4 (Slice-Zuordnung)

- **4**: `extend`-Merge + Orphan-Rule (inkl. Extend-Methoden-Generics); Interface-Konformität
  mit Signatur-Match (nicht nur Namen); Block-Lambda-Wert (gleiche Frage wie Block-Arm).
- Generics-Rest (bewusst vertagt): Constraints mit eigenen Typ-Args (`Comparable<T>` über die
  Constraint-Grenze substituieren); Monomorph-Instanzen-Sammeln → M5 (dort sitzt der Abnehmer);
  **neu entdeckt**: Tuple-Varianten-Konstruktion generischer Enums über Call (`Opt.Some(5)`)
  typt noch ohne Instanz-Inferenz — und `Opt<int>.Some(…)` ist per TypePath-Grammatik nicht
  ausdrückbar.
- Extern (nicht M4): Stdlib-Imports opak → Modul-Universum erst mit M8.

## Offene Fragen

- **Block-Wert-Entscheidung ratifizieren**: Slice 2 hat pragmatisch festgelegt, dass Blöcke
  keinen Wert haben (Block-Arm im match-Ausdruck ⇒ return/throw-Pflicht, SEM0033).
  Alternativen (Rust-Tail-Expression etc.) und Trade-offs sind diskutiert. Wenn ratifiziert:
  Satz in `Sprache.md` §5 (MatchArm) ergänzen.

## Design-Entscheidungen (Kontext)

- AST = immutable Records; Symbole = mutable Klassen; Binding/Typen via Seiten-Tabellen (Roslyn-Stil).
- Builtins als Wurzel-Scope; 2-Pass-Deklarieren; strukturierte Flow-Analyse (kein CFG).
- Typsystem-Regeln in `Sprache.md §6.5`; `ErrorType` = Poison (keine Folgefehler).
- Generics: Monomorphisierung (Sema sammelt Instanzen, Codegen → M5); strenge Constraints (D2).
- M1/M2-Kernentscheidungen: in den Tags bzw. der git-Historie.

## Letzter relevanter Commit

`M4: sema — pattern match full (slice 2)`

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
