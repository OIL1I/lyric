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
bidir. Lambda-Inferenz). **Slice 1 (Generics) komplett — 1a Fundament + 1b
Konstruktion/Inferenz/Constraints.**
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

## Woran wir gerade arbeiten

Slice 1 fertig. Nächster Slice: **M4-2 — Pattern-Match voll** (Enum-Payload-Destructuring
mit echten Typen statt M3-Poison, Struct-/Tuple-Destructuring, Or-Pattern-Konsistenz,
Exhaustivität). Noch nicht geplant.

## Was als nächstes ansteht

- M4-2 planen + bauen, dann **3** (Exceptions + Coroutinen), **4** (Closures + Interfaces +
  Extend-Merge + Orphan-Rule).

## Noch offen in M4 (Slice-Zuordnung)

- **2**: Enum-Payload-Destructuring (M3-Poison → echte Typen); `match`-Exhaustivität;
  Block-Wert-Frage (Block-Arme / Block-Lambdas).
- **4**: `extend`-Merge + Orphan-Rule (inkl. Extend-Methoden-Generics); Interface-Konformität
  mit Signatur-Match (nicht nur Namen).
- Generics-Rest (bewusst vertagt): Constraints mit eigenen Typ-Args (`Comparable<T>` über die
  Constraint-Grenze substituieren); Monomorph-Instanzen-Sammeln → M5 (dort sitzt der Abnehmer).
- Extern (nicht M4): Stdlib-Imports opak → Modul-Universum erst mit M8.

## Design-Entscheidungen (Kontext)

- AST = immutable Records; Symbole = mutable Klassen; Binding/Typen via Seiten-Tabellen (Roslyn-Stil).
- Builtins als Wurzel-Scope; 2-Pass-Deklarieren; strukturierte Flow-Analyse (kein CFG).
- Typsystem-Regeln in `Sprache.md §6.5`; `ErrorType` = Poison (keine Folgefehler).
- Generics: Monomorphisierung (Sema sammelt Instanzen, Codegen → M5); strenge Constraints (D2).
- M1/M2-Kernentscheidungen: in den Tags bzw. der git-Historie.

## Letzter relevanter Commit

`M4: generics — construction, call inference, constraints (slice 1b)`

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
