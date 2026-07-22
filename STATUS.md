# Lyric — Aktueller Stand

> Diese Datei ist die **einzige** im Projekt, die sich häufig ändert. Sie wird
> nach jedem abgeschlossenen Slice geupdatet. Claude liest sie zu
> Session-Beginn, um zu wissen, wo wir stehen.
>
> Halte den Inhalt knapp. Was schon committet ist, kann hier weg —
> `git log --oneline` ist die Historie, nicht diese Datei.

---

## Aktueller Meilenstein

**M3 — Resolver + Sema (basic)**

Slice 1 (Resolver) abgeschlossen. M2 = `m2-complete`, M1 = `m1-complete`.

## Was schon erledigt ist

- [x] **M1 — Lexer** (`m1-complete`).
- [x] **M2 — Parser** (`m2-complete`): voller AST, Recursive-Descent + Pratt, Patterns,
  `match`, if-Ausdruck, Struct-Init; `AstDumper`, CLI `lyric parse`. Codes `LYR-PAR0001..0037`.
- [x] **M3-Slice 1 — Resolver** (`Lyric.Resolver`): Symbol-Modell + Scopes, 3 Pässe
  (Deklarieren / Imports / Typ-Namen-Bindung), 17 Builtin-Typen, Duplikat-/Zyklus-/
  Sichtbarkeits-Checks, `SymbolDumper`, `Compilation` (single-file-first, Cross-Modul
  funktioniert). Seiten-Tabelle `BindingResult`. Codes `LYR-RES0001..0005`. 20 Tests.
- [x] **M3-Slice 2a — Typ-Kern** (`Lyric.Sema`): `LyrType`-Repräsentation, `TypeFacts`
  (Numerik/Fit/Anzeige), `TypeResult`-Seitentabelle, `TypeChecker`. Literale, Namen/
  Scopes (Locals/Params/Globals), Operatoren (①A strikt + ②a Literal-Fit; `+`/`*` für
  string/T[]), `as` (④), Index, Nullable-Ops (⑤), lokale Inferenz, Control-Flow-Statements.
  Codes `LYR-SEM0001..0011`. 30 Tests.

## Woran wir gerade arbeiten

**M3-Slice 2b — composite Ausdrücke** (`Lyric.Sema`): Calls (Signatur-Match), Member
-Zugriff (Field/Method/static/Modul/Enum-Variante), Struct-Init-Feldprüfung, Array/Tuple
-/f-String-Typisierung, if-Ausdruck-Unifikation, `match` (Scrutinee+Arme+Pattern-Var-Typen),
Lambda-Inferenz. (In 2a liefern diese vorerst `Error`, Sub-Ausdrücke werden aber geprüft.)

## Was als nächstes ansteht

- Slice 2b: composite Ausdrücke (s.o.).
- Slice 3: Statement/Decl-Sema (DAA, Return-Coverage, Interface-Konformität, `main`-Contract,
  Lvalue/Mutabilität, Flow-Narrowing) + CLI `lyric check` + 10+ E2E-Programme.

## Resolver-Grenzen (an Slice 2/3 übergeben)

- Nur Typ-Namen gebunden; Ausdrucks-Identifier (Locals/Calls) → Slice 2 mit dem Type-Checking.
- Externe Imports (Stdlib nicht in Compilation) sind opak, nie ein Fehler — Tippfehler im
  Modulpfad wird still „extern".
- `extend`-Methoden noch nicht in den Ziel-Typ gemerged (+ Orphan-Rule) → M4.

## Für die Sema offen (aus M2 übernommen)

Block-Wert-Frage bei Match-Block-Armen; permissiv Geparstes prüfen: `mut` nur an Methoden,
`pub` an Membern, `ExprStmt` nur Call/Assign, `params`/Default-Param-Regeln, `main`-Contract,
Exhaustivität, `for-in`-Iterator, Nullable-Regeln.

## Design-Entscheidungen (Kontext)

- AST = immutable Records; Symbole = mutable Klassen (Identität, inkrementell angereichert).
- Binding/Typen via Seiten-Tabellen (`BindingResult`/`TypeResult`, Roslyn-Stil), kein typed-AST.
- Builtins als Wurzel-Scope; 2-Pass-Deklarieren für Forward-Refs.
- **Typsystem** (`Sprache.md §6.5`): Numerik strikt (kein implizites Widening); untyped
  Literale passen sich per Range-Fit an; `+`/`*` = concat/repeat für string & T[]; `as`
  nur Numerik↔Numerik; `?T`-Widening implizit, Unwrap via `!`/`??`. `ErrorType` ist
  Poison (zu/von allem zuweisbar → keine Folgefehler).
- M2-Kernentscheidungen: im Tag `m2-complete` bzw. der git-Historie.

## Letzter relevanter Commit

`M3: sema — type system + expression checking (slice 2a)`

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
