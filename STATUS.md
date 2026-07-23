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
bidir. Lambda-Inferenz); D6–D8 ratifiziert (resume ist Ausdruck / Send-Werte post-v1 /
nur nacktes return in Coroutinen — in Sprache.md §5/§6/§8 fixiert).
**Slices 1–3 komplett.** M3 = `m3-complete`, M2 = `m2-complete`, M1 = `m1-complete`.

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
    auf jedem Pfad return/throw-en (SEM0033), im match-Statement frei. Ratifiziert,
    in `Sprache.md` §5/§6.2 festgehalten.
  - **Konstruktion (§3.4)**: Enum-Struct-Varianten qualifiziert (`Shape.Triangle { … }`)
    und kontextuell (`let s: Shape = Triangle { … }`, auch Array-Element/Return/Feld/
    Assign) via minimalem expected-Type-Threading in CheckExpr (Vorstufe von D5);
    leeres Array-Literal nimmt den Kontext-Typ.
  - Codes `LYR-SEM0029..0033` + `0050`. 50 Tests; `shapes.lyr` checkt sauber.

- [x] **M4 — Slice 3 — Exceptions + Coroutinen** (3a + 3b):
  - **Builtins**: `Throwable` als Builtin-Interface (abstraktes `message(): string`,
    Konformanz wird geprüft), `panic` → `never` (Bottom-Typ, zählt als Divergenz),
    `Coroutine<T>` als Builtin-Name → interner `CoroutineOf`.
  - **Exceptions (3a)**: Throwable-Constraint an throw/throws/catch (SEM0030);
    try braucht ≥1 catch (SEM0036), Catch-All zuletzt (SEM0035); Catch-Bindung typlos →
    `Throwable` (DAA-gefixt); **throws-Propagation** als Post-Pass (`ExceptionAnalyzer`):
    try-Zuordnung exakt/Interface/Catch-All oder eigene throws-Klausel (SEM0034);
    Lambdas/Globals/Default-Werte eigene Kontexte; throws-Fn als Wert verboten (SEM0037,
    FnType trägt keine throws-Info — Java-Lambda-Falle bewusst zum Fehler gemacht).
  - **Coroutinen (3b, D6–D8)**: `resume` ist Präfix-Ausdruck (ResumeExpr, ResumeStmt weg);
    yield nur bei `Coroutine<T>`-Rückgabetyp + wertgeprüft, nacktes yield nur
    `Coroutine<void>` (SEM0038); nur nacktes return (SEM0039); resume liefert Yield-Typ
    (SEM0040); Return-Coverage für Coroutinen ausgesetzt.
  - Codes `LYR-SEM0030, 0034..0040`. 48 Tests; `bank.lyr` + `fibonacci.lyr` checken sauber.

## Woran wir gerade arbeiten

Slice 3 fertig. Letzter M4-Slice: **M4-4 — Closures + Interfaces + Extend**
(bidir. Lambda-Inferenz D5, Block-Lambda-Wert, Interface-Konformanz mit Signatur-Match,
Extend-Merge + Orphan-Rule). Noch nicht geplant.

## Was als nächstes ansteht

- M4-4 planen + bauen → M4-Exit (`m4-complete`), dann M5 (IR + Bytecode, ADR-013 beachten).

## Noch offen in M4 (Slice-Zuordnung)

- **4**: `extend`-Merge + Orphan-Rule (inkl. Extend-Methoden-Generics); Interface-Konformität
  mit Signatur-Match (nicht nur Namen); Block-Lambda-Wert (gleiche Frage wie Block-Arm).
- Generics-Rest (bewusst vertagt): Constraints mit eigenen Typ-Args (`Comparable<T>` über die
  Constraint-Grenze substituieren); Monomorph-Instanzen-Sammeln → M5 (dort sitzt der Abnehmer);
  **neu entdeckt**: Tuple-Varianten-Konstruktion generischer Enums über Call (`Opt.Some(5)`)
  typt noch ohne Instanz-Inferenz — und `Opt<int>.Some(…)` ist per TypePath-Grammatik nicht
  ausdrückbar.
- Extern (nicht M4): Stdlib-Imports opak → Modul-Universum erst mit M8.

## Design-Entscheidungen (Kontext)

- AST = immutable Records; Symbole = mutable Klassen; Binding/Typen via Seiten-Tabellen (Roslyn-Stil).
- Builtins als Wurzel-Scope; 2-Pass-Deklarieren; strukturierte Flow-Analyse (kein CFG).
- Typsystem-Regeln in `Sprache.md §6.5`; `ErrorType` = Poison (keine Folgefehler).
- Generics: Monomorphisierung (Sema sammelt Instanzen, Codegen → M5); strenge Constraints (D2).
- M1/M2-Kernentscheidungen: in den Tags bzw. der git-Historie.

## Letzter relevanter Commit

`M4: sema — coroutines (slice 3b)`

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
