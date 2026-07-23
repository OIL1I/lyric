# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M3 — Resolver + Sema (basic) — abgeschlossen**

Alle Slices durch; `lyric check` läuft; 18 E2E-Programme (11 valide / 7 negativ) checken
sauber = M3-Exit erreicht. Offen: Tag `m3-complete`, dann M4 planen.
M2 = `m2-complete`, M1 = `m1-complete`.

## Was schon erledigt ist

- [x] **M1 — Lexer** (`m1-complete`).
- [x] **M2 — Parser** (`m2-complete`): voller AST, RD + Pratt, Patterns/`match`/if-Ausdruck/
  Struct-Init; `AstDumper`, CLI `lyric parse`. Codes `LYR-PAR0001..0037`.
- [x] **M3 — Resolver + Sema (basic)**:
  - **Resolver** (`Lyric.Resolver`): Symbole/Scopes, Deklarieren/Imports/Typ-Namen-Bindung,
    Builtins, Duplikat-/Zyklus-/Sichtbarkeits-Checks. `LYR-RES0001..0005`.
  - **TypeChecker** (`Lyric.Sema`): jeder Ausdruck typt; Numerik strikt + Literal-Fit,
    `+`/`*` für string/T[], `as`-Numerik, Nullable-Ops, Calls/Member/Struct-Init/if/match/
    Lambda. `LYR-SEM0001..0016`.
  - **Flow** (`Flow`/`FlowAnalyzer`): Return-Coverage, DAA, Nullable-Narrowing (Early-Exit).
    `LYR-SEM0017/0018`.
  - **Regeln** (`SemaRules`): Lvalue/Mutabilität, Interface-Konformität, `main`-Contract,
    Signatur-Regeln. `LYR-SEM0019..0025`. CLI `lyric check`. 89 Resolver+Sema-Tests.

## Woran wir gerade arbeiten

Nichts offen in M3. Nächster Meilenstein: **M4 — Sema (full)** (ROADMAP): Generics
(Type-Params, Constraints, Monomorphisierung), Pattern-Payload-Destructuring +
Exhaustivität, Coroutine-Sema (`yield`/`resume`), Exception-Sema (`throws`-Propagation),
`extend`-Merge + Orphan-Rule, Closure-Capture. Noch nicht geplant/geschnitten.

## Was als nächstes ansteht

- Tag `m3-complete` setzen.
- M4 planen (Slice-Schnitt).

## An M4 übergeben (bewusst in M3 vertagt)

- Enum-Payload-Destructuring: Pattern-Vars sind in M3 Poison → echte Typen in M4.
- Externe Imports (Stdlib) opak, nie ein Fehler (Modul-Universum erst mit M8).
- `extend`-Methoden noch nicht in Ziel-Typ gemerged (+ Orphan-Rule).
- Interface-Konformität prüft nur Methoden-Namen (Signatur-Match → M4).
- Block-Wert-Frage bei Match-Block-Armen / Block-Lambdas; `match`-Exhaustivität
  (M3-Return-Coverage nutzt `_`-Arm-Näherung).

## Design-Entscheidungen (Kontext)

- AST = immutable Records; Symbole = mutable Klassen; Binding/Typen via Seiten-Tabellen (Roslyn-Stil).
- Builtins als Wurzel-Scope; 2-Pass-Deklarieren; strukturierte Flow-Analyse (kein CFG).
- Typsystem-Regeln in `Sprache.md §6.5`; `ErrorType` = Poison (keine Folgefehler).
- M1/M2-Kernentscheidungen: in den Tags bzw. der git-Historie.

## Letzter relevanter Commit

`M3: sema — structural rules, lyric check, e2e (slice 3b)`

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
